﻿using System.Collections.Generic;
using System.Diagnostics;

namespace DotJEM.Json.Index.Storage
{
    public class RamFile : ILuceneFile
    {
        private readonly List<byte[]> buffers = new List<byte[]>();

        private readonly object padLock = new object();

        public long Length { get; set; }
        public long LastModified { get; set; }
        public long SizeInBytes { get; private set; }

        public RamFile()
        {
            LastModified = Stopwatch.GetTimestamp();
        }

        internal byte[] AddBuffer(int size)
        {
            byte[] numArray = new byte[size];
            lock (padLock)
            {
                buffers.Add(numArray);
                SizeInBytes += size;
            }
            return numArray;
        }

        public byte[] GetBuffer(int index)
        {
            return buffers[index];
        }

        public int NumBuffers()
        {
            return buffers.Count;
        }

        public void Delete()
        {

        }

        public long Touch()
        {
            LastModified = Stopwatch.GetTimestamp();
            return LastModified;
        }
    }
}