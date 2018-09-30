﻿using Common;
using Common.Log;
using Lykke.RabbitMqBroker;
using Lykke.RabbitMqBroker.Subscriber;
using Lykke.Job.RabbitMqToBlobUploader.Core.Services;
using System;
using System.Threading.Tasks;

namespace Lykke.Job.RabbitMqToBlobUploader.RabbitSubscribers
{
    public class RabbitSubscriber : IStopable, IMessageDeserializer<byte[]>, IMainProcessor
    {
        private const string _appEndpointName = "rabbitmqtoblobuploader";

        private readonly ILog _log;
        private readonly IBlobSaver _blobSaver;
        private readonly string _connectionString;
        private readonly string _exchangeName;
        private readonly string _routingKey;

        private RabbitMqSubscriber<byte[]> _subscriber;

        public RabbitSubscriber(
            ILog log,
            IBlobSaver blobSaver,
            string connectionString,
            string exchangeName,
            string routingKey)
        {
            _log = log;
            _blobSaver = blobSaver;
            _connectionString = connectionString;
            _exchangeName = exchangeName;
            _routingKey = routingKey;
        }

        public void Start()
        {
            _blobSaver.Start();

            string endpointName = !string.IsNullOrWhiteSpace(_routingKey)
                ? $"{_appEndpointName}.{_routingKey}"
                : _appEndpointName;

            var settings = RabbitMqSubscriptionSettings
                .ForSubscriber(_connectionString, _exchangeName, endpointName)
                .MakeDurable();

            if (!string.IsNullOrWhiteSpace(_routingKey))
                settings.UseRoutingKey(_routingKey);

            _subscriber = new RabbitMqSubscriber<byte[]>(settings,
                    new ResilientErrorHandlingStrategy(_log, settings,
                        retryTimeout: TimeSpan.FromSeconds(10),
                        next: new DeadQueueErrorHandlingStrategy(_log, settings)))
                .SetMessageDeserializer(this)
                .Subscribe(ProcessMessageAsync)
                .CreateDefaultBinding()
                .SetLogger(_log)
                .Start();
        }

        public void Stop()
        {
            _subscriber?.Stop();

            _blobSaver.Stop();
        }

        public void Dispose()
        {
            _subscriber?.Dispose();
        }

        public byte[] Deserialize(byte[] data)
        {
            return data;
        }

        private async Task ProcessMessageAsync(byte[] arg)
        {
            try
            {
                await _blobSaver.AddDataItemAsync(arg);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(RabbitSubscriber), nameof(ProcessMessageAsync), ex);
            }
        }
    }
}
