﻿using System;
using System.Collections.Generic;
using DotJEM.Json.Index.Documents;
using DotJEM.Json.Index.Documents.Fields;
using DotJEM.Json.Index.Documents.Info;
using DotJEM.Json.Index.Serialization;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Util;

namespace DotJEM.Json.Index.Configuration
{
    public interface IServiceCollection
    {
        IServiceCollection Use<TService, TImpl>() where TImpl : TService where TService : class;
        IServiceCollection Use(Type service, Type implementation);
        IServiceCollection Use<TService>(Func<TService> factory);
        IServiceCollection Use<TService>(Func<IServiceResolver, TService> factory);
        IServiceCollection Use(Type service, Func<IServiceResolver, object> factory);

        bool Contains<T>();
        bool Contains(Type type);

        Func<IServiceResolver, T> ObtainFactory<T>();
        Func<IServiceResolver, object> ObtainFactory(Type key);

        bool TryObtainFactory<T>(out Func<IServiceResolver, T> value);
        bool TryObtainFactory(Type key, out Func<IServiceResolver, object> value);
    }

    public class DefaultServiceCollection : IServiceCollection
    {
        private readonly IServiceFactory factory;
        private readonly Dictionary<Type, Func<IServiceResolver, object>> factories = new Dictionary<Type, Func<IServiceResolver, object>>();

        public static IServiceCollection CreateDefault()
        {
            return new DefaultServiceCollection()
                .Use<Analyzer>(() => new StandardAnalyzer(LuceneVersion.LUCENE_48, CharArraySet.EMPTY_SET))
                .Use<IFieldResolver, FieldResolver>()
                .Use<IFieldInformationManager, DefaultFieldInformationManager>()
                .Use<ILuceneDocumentFactory, LuceneDocumentFactory>()
                .Use<IJsonSerializer, GZipJsonSerialier>();
        }

        public DefaultServiceCollection(IServiceFactory factory = null)
        {
            this.factory = factory ?? new DefaultServiceFactory();
        }
        public bool TryObtainFactory<T>(out Func<IServiceResolver, T> value)
        {
            if (TryObtainFactory(typeof(T), out Func<IServiceResolver, object> fac))
            {
                value = sp => (T)fac(sp);
                return true;
            }
            value = null;
            return false;
        }

        public virtual bool TryObtainFactory(Type key, out Func<IServiceResolver, object> value) => factories.TryGetValue(key, out value);
        public Func<IServiceResolver, T> ObtainFactory<T>() => TryObtainFactory<T>(out var fac) ? fac : null;
        public virtual Func<IServiceResolver, object> ObtainFactory(Type key) => TryObtainFactory(key, out var fac) ? fac : null;

        public virtual IServiceCollection Use<TService, TImpl>() where TImpl : TService where TService : class => Use(typeof(TService), typeof(TImpl));
        public virtual IServiceCollection Use(Type service, Type implementation) => Use(service, provider => factory.Create(provider, implementation));
        public virtual IServiceCollection Use<TService>(Func<TService> factoryMethod) => Use(typeof(TService), sp => factoryMethod());
        public virtual IServiceCollection Use<TService>(Func<IServiceResolver, TService> factoryMethod) => Use(typeof(TService), sp => factoryMethod(sp));

        public virtual IServiceCollection Use(Type service, Func<IServiceResolver, object> factoryMethod)
        {
            factories[service] = factoryMethod;
            return this;
        }

        public virtual bool Contains<TService>() => Contains(typeof(TService));

        public virtual bool Contains(Type type) => factories.ContainsKey(type);
    }


}