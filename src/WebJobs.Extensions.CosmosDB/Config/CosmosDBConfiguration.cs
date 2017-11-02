﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Azure.Documents.ChangeFeedProcessor;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.WebJobs.Extensions.CosmosDB.Bindings;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Config;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.CosmosDB
{
    /// <summary>
    /// Defines the configuration options for the CosmosDB binding.
    /// </summary>
    public class CosmosDBConfiguration : IExtensionConfigProvider
    {
        internal const string AzureWebJobsCosmosDBConnectionStringName = "AzureWebJobsCosmosDBConnectionString";
        internal readonly ConcurrentDictionary<string, ICosmosDBService> ClientCache = new ConcurrentDictionary<string, ICosmosDBService>();
        private string _defaultConnectionString;
        private TraceWriter _trace;

        /// <summary>
        /// Constructs a new instance.
        /// </summary>
        public CosmosDBConfiguration()
        {
            CosmosDBServiceFactory = new DefaultCosmosDBServiceFactory();
        }

        internal ICosmosDBServiceFactory CosmosDBServiceFactory { get; set; }

        /// <summary>
        /// Gets or sets the CosmosDB connection string.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the lease options for the DocumentDB Trigger. 
        /// </summary>
        public ChangeFeedHostOptions LeaseOptions { get; set; }

        /// <inheritdoc />
        public void Initialize(ExtensionConfigContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _trace = context.Trace;

            INameResolver nameResolver = context.Config.GetService<INameResolver>();

            IConverterManager converterManager = context.Config.GetService<IConverterManager>();

            // Use this if there is no other connection string set.
            _defaultConnectionString = nameResolver.Resolve(AzureWebJobsCosmosDBConnectionStringName);

            BindingFactory factory = new BindingFactory(nameResolver, converterManager);

            IBindingProvider outputProvider = factory.BindToCollector<CosmosDBAttribute, OpenType>(typeof(CosmosDBCollectorBuilder<>), this);

            IBindingProvider outputProviderJObject = factory.BindToCollector<CosmosDBAttribute, JObject>(typeof(CosmosDBCollectorBuilder<>), this);

            IBindingProvider clientProvider = factory.BindToInput<CosmosDBAttribute, DocumentClient>(new CosmosDBClientBuilder(this));

            IBindingProvider jArrayProvider = factory.BindToInput<CosmosDBAttribute, JArray>(typeof(CosmosDBJArrayBuilder), this);

            IBindingProvider enumerableProvider = factory.BindToInput<CosmosDBAttribute, IEnumerable<OpenType>>(typeof(CosmosDBEnumerableBuilder<>), this);
            enumerableProvider = factory.AddValidator<CosmosDBAttribute>(ValidateInputBinding, enumerableProvider);

            IBindingProvider inputProvider = factory.BindToGenericValueProvider<CosmosDBAttribute>((attr, t) => BindForItemAsync(attr, t));
            inputProvider = factory.AddValidator<CosmosDBAttribute>(ValidateInputBinding, inputProvider);

            context.AddBindingRule<CosmosDBAttribute>()
                .AddConverter<JObject, JObject>(s => s);

            IExtensionRegistry extensions = context.Config.GetService<IExtensionRegistry>();
            extensions.RegisterBindingRules<CosmosDBAttribute>(ValidateConnection, nameResolver, outputProvider, outputProviderJObject, clientProvider, jArrayProvider, enumerableProvider, inputProvider);

            context.Config.RegisterBindingExtensions(new CosmosDBTriggerAttributeBindingProvider(nameResolver, this, LeaseOptions));
        }

        internal static void ValidateInputBinding(CosmosDBAttribute attribute, Type parameterType)
        {
            bool hasSqlQuery = !string.IsNullOrEmpty(attribute.SqlQuery);
            bool hasId = !string.IsNullOrEmpty(attribute.Id);

            if (hasSqlQuery && hasId)
            {
                throw new InvalidOperationException($"Only one of 'SqlQuery' and '{nameof(CosmosDBAttribute.Id)}' can be specified.");
            }

            if (IsSupportedEnumerable(parameterType))
            {
                if (hasId)
                {
                    throw new InvalidOperationException($"'{nameof(CosmosDBAttribute.Id)}' cannot be specified when binding to an IEnumerable property.");
                }
            }
            else if (!hasId)
            {
                throw new InvalidOperationException($"'{nameof(CosmosDBAttribute.Id)}' is required when binding to a {parameterType.Name} property.");
            }
        }

        internal void ValidateConnection(CosmosDBAttribute attribute, Type paramType)
        {
            if (string.IsNullOrEmpty(ConnectionString) &&
                string.IsNullOrEmpty(attribute.ConnectionStringSetting) &&
                string.IsNullOrEmpty(_defaultConnectionString))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture,
                    "The CosmosDB connection string must be set either via a '{0}' app setting, via the CosmosDBAttribute.ConnectionStringSetting property or via CosmosDBConfiguration.ConnectionString.",
                    AzureWebJobsCosmosDBConnectionStringName));
            }
        }

        internal DocumentClient BindForClient(CosmosDBAttribute attribute)
        {
            string resolvedConnectionString = ResolveConnectionString(attribute.ConnectionStringSetting);
            ICosmosDBService service = GetService(resolvedConnectionString);

            return service.GetClient();
        }

        internal Task<IValueBinder> BindForItemAsync(CosmosDBAttribute attribute, Type type)
        {
            if (string.IsNullOrEmpty(attribute.Id))
            {
                throw new InvalidOperationException("The 'Id' property of a CosmosDB single-item input binding cannot be null or empty.");
            }

            CosmosDBContext context = CreateContext(attribute);

            Type genericType = typeof(CosmosDBItemValueBinder<>).MakeGenericType(type);
            IValueBinder binder = (IValueBinder)Activator.CreateInstance(genericType, context);

            return Task.FromResult(binder);
        }

        internal string ResolveConnectionString(string attributeConnectionString)
        {
            // First, try the Attribute's string.
            if (!string.IsNullOrEmpty(attributeConnectionString))
            {
                return attributeConnectionString;
            }

            // Second, try the config's ConnectionString
            if (!string.IsNullOrEmpty(ConnectionString))
            {
                return ConnectionString;
            }

            // Finally, fall back to the default.
            return _defaultConnectionString;
        }

        internal ICosmosDBService GetService(string connectionString)
        {
            return ClientCache.GetOrAdd(connectionString, (c) => CosmosDBServiceFactory.CreateService(c));
        }

        internal CosmosDBContext CreateContext(CosmosDBAttribute attribute)
        {
            string resolvedConnectionString = ResolveConnectionString(attribute.ConnectionStringSetting);

            ICosmosDBService service = GetService(resolvedConnectionString);

            return new CosmosDBContext
            {
                Service = service,
                Trace = _trace,
                ResolvedAttribute = attribute,
            };
        }

        internal static bool IsSupportedEnumerable(Type type)
        {
            if (type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return true;
            }

            return false;
        }
    }
}