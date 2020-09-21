﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using DotJEM.Json.Index;
using DotJEM.Json.Index.Documents;
using DotJEM.Json.Index.Inflow;
using DotJEM.Json.Index.Ingest;
using DotJEM.Json.Index.IO;
using DotJEM.Json.Index.Manager;
using DotJEM.Json.Index.QueryParsers;
using DotJEM.Json.Index.Results;
using DotJEM.Json.Storage;
using DotJEM.Json.Storage.Adapter;
using DotJEM.Json.Storage.Adapter.Materialize.ChanceLog;
using DotJEM.Json.Storage.Adapter.Materialize.ChanceLog.ChangeObjects;
using DotJEM.Json.Storage.Adapter.Materialize.Log;
using Lucene.Net.Index;
using Lucene.Net.Util;
using Newtonsoft.Json.Linq;

namespace Ingest
{
    class Program
    {
        static void Main(string[] args)
        {
            string path = "G:\\INDEX";
            Directory.CreateDirectory(path);
            foreach (string file in Directory.GetFiles(path))
                File.Delete(file);

            //IndexWriterManager.DEFAULT_RAM_BUFFER_SIZE_MB = 1024 * 8; // 8GB.

            IStorageContext context = new SqlServerStorageContext("Data Source=.\\DEV;Initial Catalog=SSN3DB;Integrated Security=True");

            IStorageArea[] areas = context
                .AreaInfos
                .Where(info => info.Name != "audit")
                .Select(info => context.Area(info.Name))
                .ToArray();

            DbInloadingInflowCapacity cap  = null;
            LuceneJsonIndexBuilder builder = new LuceneJsonIndexBuilder("main");
            builder.UseSimpleFileStorage(path);
            builder.Services.Use<ILuceneQueryParser, SimplifiedLuceneQueryParser>();
            builder.Services.Use<IInflowCapacity>(
                resolver =>
                {
                    ILuceneJsonIndex idx = resolver.Resolve<ILuceneJsonIndex>();
                    return cap = new DbInloadingInflowCapacity(areas, idx);
                });
            ILuceneJsonIndex index = builder.Build();

            index.CreateWriter();
            cap.Initialize(10);


            //IngestManager ingest = new IngestManager(new SimpleCountingCapacityControl(), new StorageAreaInloadSource(areas, index));
            //ingest.Start();

            //index.CreateWriter().Inflow.Scheduler.Enqueue(new DbInloadingInflowCapacity(areas,index), Priority.Highest);

            Stopwatch timer = Stopwatch.StartNew();

            while (!IsExit(Console.ReadLine()))
            {
                CheckSources(null, null);
            }


            bool IsExit(string str)
            {
                if ("Q;QUIT;EXIT".Split(';').Any(s => s.Equals(str, StringComparison.OrdinalIgnoreCase)))
                    return true;

                if (string.IsNullOrEmpty(str))
                    return false;


                try
                {
                    Stopwatch searchtimer = Stopwatch.StartNew();
                    SearchResults results = index.Search(str).Take(25).Execute().Result;
                    Console.WriteLine($"Search for '{str}' resulted in {results.TotalHits} results in {searchtimer.ElapsedMilliseconds} ms...");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                return false;
            }

            void CheckSources(object sender, EventArgs eventArgs)
            {
                IngestManager.pause = true;

                TimeSpan elapsed = timer.Elapsed;
                //int ready = sources.Count(s => s.Ready);
                //Console.WriteLine($"{ready} Sources Ready of {sources.Length}");
                Console.WriteLine($"Elapsed time: {elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s {elapsed.Milliseconds}f");
                Metrics.Print();
                //ingest.CheckCapacity();

                Console.WriteLine($"Index cached ram: {index.WriterManager.Writer.RamSizeInBytes()}");
                index.WriterManager.Writer.Flush(true, true);
                index.WriterManager.Writer.Commit();

                IngestManager.pause = false;
            }
        }
    }


    public class DbInloadingInflowCapacity : IInflowCapacity
    {
        private int count;

        private StorageAreaInloadSource source;

        public DbInloadingInflowCapacity(IStorageArea[] areas, ILuceneJsonIndex index)
        {
            this.source = new StorageAreaInloadSource(areas, index);
        }

        public DbInloadingInflowCapacity Initialize(int count)
        {
            for (int i = 0; i < count; i++)
                source.LoadData();
            return this;
        }

        public void Free(int estimatedCost)
        {
            if (Interlocked.Decrement(ref this.count) < 10)
                source.LoadData();
        }

        public void Allocate(int estimatedCost)
        {
            Interlocked.Increment(ref this.count);
        }
    }

    public class StorageAreaInloadSource : IIngestInload
    {
        private int i = 0;
        private readonly IStorageArea[] areas;
        private readonly ILuceneJsonIndex index;

        public StorageAreaInloadSource(IStorageArea[] areas, ILuceneJsonIndex index)
        {
            this.areas = areas;
            this.index = index;
        }

        public void LoadData()
        {
            index.CreateWriter().Inflow.Scheduler.Enqueue(new LoadInflowJob(index, FlipArea()), Priority.Highest);
        }

        public IAsyncJob CreateInloadJob()
        {
            Console.WriteLine("Creating a new Inload job");
            IStorageArea area = FlipArea();
            return new StorageAreaLoad(area, index);
        }

        private IStorageArea FlipArea()
        {
            IStorageArea area = areas[i++];
            i %= areas.Length;
            return area;
        }
    }

    public class LoadInflowJob : IInflowJob
    {
        private readonly IStorageArea area;
        private readonly IInflowManager inflow;
        private readonly ILuceneJsonIndex index;

        public LoadInflowJob(ILuceneJsonIndex index, IStorageArea area)
        {
            this.index = index;
            this.area = area;
            this.inflow = index.CreateWriter().Inflow;
        }

        public int EstimatedCost { get; } = 1;

        public void Execute(IInflowScheduler scheduler)
        {
            IStorageChangeCollection changes = area.Log.Get(includeDeletes: false, count: 5000);
            if (changes.Count < 1)
                return;



            if (changes.Count.Created > 0)
            {
                IReservedSlot createdSlot = inflow.Queue.Reserve((manager, entries) =>
                {
                    manager.Writer.AddDocuments(entries.Select(e => e.Document));
                }, area.Name);
                scheduler.Enqueue(new DeserializeInflowJob(createdSlot,index, changes.Created), Priority.High);
            }
            if (changes.Count.Updated > 0) 
            {
                IReservedSlot createdSlot = inflow.Queue.Reserve((manager, entries) =>
                {
                    foreach (LuceneDocumentEntry entry in entries)
                        manager.Writer.UpdateDocument(entry.Key, entry.Document);
                }, area.Name);
                scheduler.Enqueue(new DeserializeInflowJob(createdSlot,index,  changes.Created), Priority.Medium);
            }
            if (changes.Count.Deleted > 0) 
            {
                IReservedSlot createdSlot = inflow.Queue.Reserve((manager, entries) =>
                {
                    manager.Writer.DeleteDocuments(entries.Select(e => e.Key).ToArray());
                }, area.Name);
                scheduler.Enqueue(new DeserializeInflowJob(createdSlot,index,  changes.Created), Priority.Low);
            }
        }
    }

    public class DeserializeInflowJob : IInflowJob
    {
        private readonly IReservedSlot slot;
        private readonly IEnumerable<IChangeLogRow> changes;
        private readonly IInflowManager inflow;
        private readonly ILuceneJsonIndex index;

        public DeserializeInflowJob(IReservedSlot slot, ILuceneJsonIndex index, IEnumerable<IChangeLogRow> changes)
        {
            this.slot = slot;
            this.changes = changes;
            this.index = index;
            this.inflow = index.CreateWriter().Inflow;
        }

        public int EstimatedCost { get; }
        public void Execute(IInflowScheduler scheduler)
        {
            JObject[] objects = changes
                .Select(change => change.CreateEntity())
                .ToArray();
            scheduler.Enqueue(new ConvertInflow(slot, objects, index.CreateWriter().Factory), Priority.Medium);

            if (rand.Next(10) > 8)
            {
                IReservedSlot commitSlot = inflow.Queue.Reserve((manager, entries) =>
                {
                    manager.Writer.Flush(true, true);
                    manager.Writer.Commit();
                });
                scheduler.Enqueue(new CommitInflowJob(commitSlot, index.WriterManager), Priority.Highest);
            }


        }
        private static readonly Random rand = new Random();
    }

    public class CommitInflowJob : IInflowJob
    {
        private readonly IReservedSlot slot;
        private readonly IIndexWriterManager writer;

        public CommitInflowJob(IReservedSlot slot, IIndexWriterManager writer)
        {
            this.slot = slot;
            this.writer = writer;
        }

        public int EstimatedCost { get; } = 1;
        public void Execute(IInflowScheduler scheduler)
        {
            slot.Complete(writer);
        }
    }

    public class StorageAreaLoad : IAsyncJob
    {
        private readonly IStorageArea area;
        private readonly ILuceneJsonIndex index;

        public long Cost { get; } = 1;

        public StorageAreaLoad(IStorageArea area, ILuceneJsonIndex index)
        {
            this.area = area;
            this.index = index;
            Metrics.Initiate(nameof(StorageAreaLoad));
        }

        public void Execute(IIngestScheduler ingestScheduler)
        {
            IStorageChangeCollection changes = area.Log.Get(includeDeletes: false, count: 2500);
            if (changes.Count < 1)
                return;

            Metrics.Complete(nameof(StorageAreaLoad));
            Console.WriteLine($"[{area.Name}] Loading {changes} changes ({changes.Generation}/{area.Log.LatestGeneration})");

            if (changes.Count.Created > 0) ingestScheduler.Enqueue(new Materialize(IngestFlowControl.ReserveSlot(area.Name), index, ChangeType.Create, changes.Created), JobPriority.Low);
            if (changes.Count.Updated > 0) ingestScheduler.Enqueue(new Materialize(IngestFlowControl.ReserveSlot(area.Name), index, ChangeType.Update, changes.Updated), JobPriority.Low);
            if (changes.Count.Deleted > 0) ingestScheduler.Enqueue(new Materialize(IngestFlowControl.ReserveSlot(area.Name), index, ChangeType.Delete, changes.Deleted), JobPriority.Low);
        }

        ~StorageAreaLoad()
        {
            Metrics.Finalize(nameof(StorageAreaLoad));
        }
    }

    public class Materialize : IAsyncJob
    {
        private readonly Slot slot;
        private readonly ChangeType type;
        private readonly ILuceneJsonIndex index;
        private readonly List<IChangeLogRow> changes;

        public long Cost => changes.Count;

        public Materialize(Slot slot, ILuceneJsonIndex index, ChangeType type, IEnumerable<IChangeLogRow> changes)
        {
            this.slot = slot;
            this.index = index;
            this.type = type;
            this.changes = changes.ToList();
            Metrics.Initiate(nameof(Materialize));
        }

        public void Execute(IIngestScheduler ingestScheduler)
        {
            Console.WriteLine($"Executing {GetType()} materializing {changes.Count} objects...");
            JObject[] objects = changes
                .Select(change => change.CreateEntity())
                .ToArray();
            Metrics.Complete(nameof(Materialize));
            ingestScheduler.Enqueue(CreateJob(objects), JobPriority.High);
        }

        private IAsyncJob CreateJob(JObject[] objects)
        {
            switch (type)
            {
                case ChangeType.Create:
                    return new CreateWriteJob(slot, index, objects);
                case ChangeType.Update:
                    return new UpdateWriteJob(slot, index, objects);
                case ChangeType.Delete:
                    return new DeleteWriteJob(slot, index, objects);
            }
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        ~Materialize()
        {
            Metrics.Finalize(nameof(Materialize));
        }
    }

    public abstract class WriteJob : IAsyncJob
    {
        private static readonly Random rand = new Random();

        private readonly Slot slot;
        private readonly ILuceneJsonIndex index;
        private readonly JObject[] objects;
        private readonly string name;

        public long Cost => objects.LongLength;

        protected WriteJob(Slot slot, ILuceneJsonIndex index, JObject[] objects)
        {
            this.slot = slot;
            this.index = index;
            this.objects = objects;
            this.name = this.GetType().Name;

            Metrics.Initiate(name);
        }

        public void Execute(IIngestScheduler ingestScheduler)
        {
            try
            {
                Console.WriteLine($"Executing {GetType()} writing {objects.Length} objects...");
                Write(objects, index.CreateWriter());
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            Metrics.Complete(name);

            if(rand.Next(10) > 8) ingestScheduler.Enqueue(new CommitJob(slot, index), JobPriority.High );
        }

        protected abstract void Write(JObject[] objects, IJsonIndexWriter writer);

        ~WriteJob()
        {
            Metrics.Finalize(name);
        }
    }

    public class CommitJob : IAsyncJob
    {
        private readonly Slot slot;
        private readonly ILuceneJsonIndex index;

        public long Cost { get; } = 1;

        public CommitJob(Slot slot, ILuceneJsonIndex index)
        {
            this.slot = slot;
            this.index = index;
            Metrics.Initiate(nameof(CommitJob));
        }

        public void Execute(IIngestScheduler ingestScheduler)
        {
            Console.WriteLine($"Executing {GetType()} committing objects...");
            index.CreateWriter().Commit();
            Metrics.Complete(nameof(CommitJob));
        }

        ~CommitJob()
        {
            Metrics.Finalize(nameof(CommitJob));
        }
    }

    public class CreateWriteJob : WriteJob
    {
        public CreateWriteJob(Slot slot,ILuceneJsonIndex index, JObject[] objects) : base(slot,index, objects)
        {
        }

        protected override void Write(JObject[] objects, IJsonIndexWriter writer)
        {
            writer.Create(objects);
        }
    }

    public class UpdateWriteJob : WriteJob
    {
        public UpdateWriteJob(Slot slot,ILuceneJsonIndex index, JObject[] objects) : base(slot, index, objects)
        {
        }

        protected override void Write(JObject[] objects, IJsonIndexWriter writer)
        {
            writer.Update(objects);
        }
    }

    public class DeleteWriteJob : WriteJob
    {
        public DeleteWriteJob(Slot slot,ILuceneJsonIndex index, JObject[] objects) : base(slot, index, objects)
        {
        }

        protected override void Write(JObject[] objects, IJsonIndexWriter writer)
        {
            writer.Delete(objects);
        }
    }

    public class IngestFlowControl
    {
        private static readonly ConcurrentDictionary<string, Queue<Slot>> slots = new ConcurrentDictionary<string, Queue<Slot>>();

        public static Slot ReserveSlot(string key)
        {
            Slot slot = new Slot();
            slots.AddOrUpdate(key, s => new Queue<Slot>(new []{slot}), (s, queue) =>
            {
                queue.Enqueue(slot);
                return queue;
            });
            return slot;
        }
    }

    public class Slot
    {
        private Guid Uuid { get; } = Guid.NewGuid();

        public void Finalize()
        {

        }
    }


}
