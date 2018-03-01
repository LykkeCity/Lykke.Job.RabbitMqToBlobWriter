﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Common.Log;
using Lykke.Job.RabbitMqToBlobUploader.Core.Services;

namespace Lykke.Job.RabbitMqToBlobUploader.Services
{
    public class BlobSaver : IBlobSaver
    {
        private const int _warningQueueCount = 1000;
        private const int _maxBlockSize = 4 * 1024 * 1024; // 4 Mb
        private const string _hourFormat = "yyyy-MM-dd-HH";
        private const string _dateFormat = "yyyy-MM-dd";

        private readonly ILog _log;
        private readonly CloudBlobContainer _blobContainer;
        private readonly List<Tuple<DateTime, byte[]>> _queue = new List<Tuple<DateTime, byte[]>>();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly int _minBatchCount;
        private readonly int _maxBatchCount;
        private readonly bool _useBatchingByHour;
        private readonly TimeSpan _delay = TimeSpan.FromMilliseconds(500);
        private readonly BlobRequestOptions _blobRequestOptions = new BlobRequestOptions
        {
            MaximumExecutionTime = TimeSpan.FromMinutes(15),
        };
        private readonly Encoding _blobEncoding = Encoding.UTF8;
        private readonly byte[] _eolBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

        private Thread _thread;
        private CancellationTokenSource _cancellationTokenSource;
        private DateTime? _lastTime;
        private DateTime _lastWarning = DateTime.MinValue;
        private CloudAppendBlob _blob;

        public BlobSaver(
            ILog log,
            IShutdownManager shutdownManager,
            string blobConnectionString,
            string container,
            bool isPublicContainer,
            bool useBatchingByHour,
            int minBatchCount,
            int maxBatchCount)
        {
            _log = log;
            _useBatchingByHour = useBatchingByHour;
            _minBatchCount = minBatchCount > 0 ? minBatchCount : 10;
            _maxBatchCount = maxBatchCount > 0 ? maxBatchCount : 1000;

            var storageAccount = CloudStorageAccount.Parse(blobConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            _blobContainer = blobClient.GetContainerReference(container.Replace('.', '-').ToLower());
            bool containerExists = _blobContainer.ExistsAsync().GetAwaiter().GetResult();
            if (!containerExists)
                _blobContainer
                    .CreateAsync(
                        isPublicContainer ? BlobContainerPublicAccessType.Container : BlobContainerPublicAccessType.Off, null, null)
                    .GetAwaiter()
                    .GetResult();

            shutdownManager.Register(this, 1);
        }

        public async Task AddDataItemAsync(byte[] item)
        {
            int count;
            await _lock.WaitAsync();
            try
            {
                _queue.Add(new Tuple<DateTime, byte[]>(DateTime.UtcNow, item));
                count = _queue.Count;
            }
            finally
            {
                _lock.Release();
            }

            if (count <= _warningQueueCount)
                return;

            var now = DateTime.UtcNow;
            if (now.Subtract(_lastWarning) >= TimeSpan.FromMinutes(1))
            {
                _lastWarning = now;
                await _log.WriteWarningAsync(
                    nameof(BlobSaver),
                    nameof(AddDataItemAsync),
                    $"{count} items in saving queue (> {_warningQueueCount}) - thread status: {(_thread != null ? _thread.ThreadState.ToString() : "missing")}");
            }
        }

        public void Start()
        {
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                _cancellationTokenSource = new CancellationTokenSource();

            if (_thread != null)
                return;

            _thread = new Thread(ProcessData) { Name = "RabbitMqToBlobUploader" };
            _thread.Start();
        }

        public void Stop()
        {
            if (IsStopped())
                return;

            var thread = _thread;
            if (thread == null)
                return;

            _thread = null;
            _cancellationTokenSource?.Cancel();

            while (true)
            {
                _lock.Wait();
                try
                {
                    if (_queue.Count == 0)
                        break;
                }
                finally
                {
                    _lock.Release();
                }
                Thread.Sleep(1000);
            }

            thread.Join();

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void ProcessData()
        {
            ProcessDataAsync().GetAwaiter().GetResult();
        }

        private async Task ProcessDataAsync()
        {
            while (true)
            {
                try
                {
                    await ProcessQueueAsync();
                }
                catch (Exception ex)
                {
                    await _log.WriteErrorAsync(nameof(BlobSaver), nameof(ProcessDataAsync), ex);
                }
            }
        }

        private async Task ProcessQueueAsync()
        {
            int itemsCount = _queue.Count;
            if (_queue.Count > _warningQueueCount)
                await _log.WriteInfoAsync(nameof(BlobSaver), nameof(ProcessQueueAsync), $"{itemsCount} items in queue");
            if (itemsCount == 0
                || itemsCount < _minBatchCount
                && _lastTime.HasValue
                && DateTime.UtcNow.Subtract(_lastTime.Value) < TimeSpan.FromHours(1)
                && (_cancellationTokenSource == null || !_cancellationTokenSource.IsCancellationRequested))
            {
                await Task.Delay(_delay, _cancellationTokenSource.Token);
                return;
            }

            Tuple<DateTime, byte[]> pair;
            int count = 0;
            while (count < _maxBatchCount && count < itemsCount)
            {
                pair = _queue[count];
                if (!_lastTime.HasValue)
                    _lastTime = pair.Item1;
                if (pair.Item1.Date != _lastTime.Value.Date || _useBatchingByHour && pair.Item1.Hour != _lastTime.Value.Hour)
                {
                    if (count == 0)
                    {
                        _lastTime = pair.Item1;
                        _blob = null;
                    }
                    else
                    {
                        break;
                    }
                }
                ++count;
            }

            if (count == 0)
                return;

            await SaveQueueAsync(count);
        }

        private async Task SaveQueueAsync(int count)
        {
            int i;
            int allLength = 0;
            for (i = 0; i < count; ++i)
            {
                allLength += 2 + _queue[i].Item2.Length;
                if (allLength > _maxBlockSize)
                    break;
            }

            if (i == 0)
            {
                await _log.WriteErrorAsync(
                    "BlobSaver.SaveQueueAsync",
                    _queue[0].Item2.Length.ToString(),
                    new InvalidOperationException("Could not append new block. Item is too large!"));
                await _lock.WaitAsync();
                try
                {
                    _queue.RemoveAt(0);
                }
                finally
                {
                    _lock.Release();
                }
                return;
            }

            if (_blob == null)
            {
                string blobKey = _queue[0].Item1.ToString(_useBatchingByHour ? _hourFormat : _dateFormat);
                await InitBlobAsync(blobKey);

                if (_queue.Count > _warningQueueCount)
                    await _log.WriteInfoAsync(
                        nameof(BlobSaver),
                        nameof(SaveQueueAsync),
                        "Blob was recreated - " + (_blob?.Uri != null ? _blob.Uri.ToString() : ""));
            }

            try
            {
                await SaveToBlobAsync(i);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync($"BlobSaver.SaveQueueAsync", i.ToString(), ex);
                if (ex is StorageException)
                    _blob = null;
            }
        }

        private async Task SaveToBlobAsync(int count)
        {
            using (var stream = new MemoryStream())
            {
                for (int j = 0; j < count; j++)
                {
                    var data = _queue[j].Item2;
                    stream.Write(data, 0, data.Length);
                    stream.Write(_eolBytes, 0, _eolBytes.Length);
                }
                stream.Position = 0;
                await _blob.AppendFromStreamAsync(stream, null, _blobRequestOptions, null);
            }

            bool isLocked = await _lock.WaitAsync(TimeSpan.FromSeconds(1));
            if (isLocked)
            {
                try
                {
                    _queue.RemoveRange(0, count);
                }
                finally
                {
                    _lock.Release();
                }
            }
            else
            {
                await _log.WriteWarningAsync(nameof(BlobSaver), nameof(SaveToBlobAsync), "Using unsafe queue clearing");
                _queue.RemoveRange(0, count);
            }

            if (_queue.Count > _warningQueueCount)
                await _log.WriteInfoAsync(
                    nameof(BlobSaver),
                    nameof(SaveToBlobAsync),
                    $"{count} items were saved to " + (_blob?.Uri != null ? _blob.Uri.ToString() : ""));
        }

        private async Task InitBlobAsync(string storagePath)
        {
            _blob = _blobContainer.GetAppendBlobReference(storagePath);
            if (await _blob.ExistsAsync())
                return;

            try
            {
                await _blob.CreateOrReplaceAsync(AccessCondition.GenerateIfNotExistsCondition(), null, null);
                _blob.Properties.ContentType = "text/plain";
                _blob.Properties.ContentEncoding = _blobEncoding.WebName;
                await _blob.SetPropertiesAsync(null, _blobRequestOptions, null);
            }
            catch (StorageException)
            {
            }
        }

        private bool IsStopped()
        {
            return _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested;
        }
    }
}