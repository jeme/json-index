﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using DotJEM.Json.Index.Configuration;
using DotJEM.Json.Index.Documents.Builder;
using DotJEM.Json.Index.Documents.Fields;
using DotJEM.Json.Index.Documents.Info;
using Lucene.Net.Documents;
using Newtonsoft.Json.Linq;

namespace DotJEM.Json.Index.Documents
{
    public interface ILuceneDocumentFactory
    {
        Task<LuceneDocumentEntry> Create(JObject entity);
        IEnumerable<LuceneDocumentEntry> Create(IEnumerable<JObject> entity);
    }

    public class LuceneDocumentFactory : ILuceneDocumentFactory
    {
        private readonly IFieldInformationManager fieldsInfo;
        private readonly IFactory<ILuceneDocumentBuilder> builderFactory;

        public LuceneDocumentFactory(IFieldInformationManager fieldsInformationManager)
            : this(fieldsInformationManager, new FuncFactory<ILuceneDocumentBuilder>(() => new LuceneDocumentBuilder()))
        {
        }

        public LuceneDocumentFactory(IFieldInformationManager fieldsInformationManager, IFactory<ILuceneDocumentBuilder> builderFactory)
        {
            this.fieldsInfo = fieldsInformationManager ?? throw new ArgumentNullException(nameof(fieldsInformationManager));
            this.builderFactory = builderFactory ?? throw new ArgumentNullException(nameof(builderFactory));
        }

        public async Task<LuceneDocumentEntry> Create(JObject entity)
        {
            return await Task.Run(async () =>
            {
                ILuceneDocumentBuilder builder = builderFactory.Create();
                string contentType = fieldsInfo.Resolver.ContentType(entity);

                Document doc = builder.Build(entity);
                await fieldsInfo.Merge(contentType, builder.FieldInfo);

                return new LuceneDocumentEntry(fieldsInfo.Resolver.Identity(entity), contentType, doc);
            });
        }

        public IEnumerable<LuceneDocumentEntry> Create(IEnumerable<JObject> docs)
        {
            BlockingCollectionEnumerable<LuceneDocumentEntry> collection = new BlockingCollectionEnumerable<LuceneDocumentEntry>();
            #pragma warning disable 4014
            FillAsync(docs, collection);
            #pragma warning restore 4014
            return collection;
        }

        private async Task FillAsync(IEnumerable<JObject> docs, BlockingCollectionEnumerable<LuceneDocumentEntry> collection)
        {
            await Task.WhenAll(docs.Select(async (json, index) =>
            {
                try
                {
                    collection.Add(await Create(json));
                }
                catch (Exception e)
                {
                    collection.ReportFailure(e, json);
                }
            }));
            collection.CompleteAdding();
        }

        private class BlockingCollectionEnumerable<T> : IEnumerable<T> where T : class
        {
            private bool complete = false;
            private readonly BlockingCollection<T> collection = new BlockingCollection<T>();
            private readonly List<(Exception, JObject)> failures = new List<(Exception, JObject)>();

            public void Add(T item)
            {
                collection.Add(item);
            }

            public void CompleteAdding()
            {
                collection.CompleteAdding();
            }

            public IEnumerator<T> GetEnumerator()
            {
                while (!collection.IsCompleted)
                {
                    T value = null;
                    try
                    {
                        value = collection.Take();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }

                    if (value != null)
                    {
                        yield return value;
                    }

                    if (failures.Any())
                    {
                        ExceptionDispatchInfo.Capture(failures.Select(f => f.Item1).First()).Throw();
                    }
                }

            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void ReportFailure(Exception exception, JObject json)
            {
                failures.Add((exception, json));
                //TODO: We need a way to report errors up stream... (Exception or similar).
            }
        }
    }
}