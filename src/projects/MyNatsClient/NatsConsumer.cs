﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MyNatsClient.Internals;
using MyNatsClient.Internals.Extensions;
using MyNatsClient.Ops;

namespace MyNatsClient
{
    public class NatsConsumer : IDisposable, INatsConsumer
    {
        private bool _isDisposed;
        private readonly INatsClient _client;
        private readonly ConcurrentDictionary<string, IConsumerSubscription> _subscriptions;

        public NatsConsumer(INatsClient client)
        {
            _client = client;
            _subscriptions = new ConcurrentDictionary<string, IConsumerSubscription>();

        }

        public void Dispose()
        {
            ThrowIfDisposed();

            Dispose(true);
            GC.SuppressFinalize(this);
            _isDisposed = true;
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed || !disposing)
                return;

            var subscriptions = _subscriptions.Values.Cast<IDisposable>().ToArray();
            if (!subscriptions.Any())
                return;

            _subscriptions.Clear();
            Try.DisposeAll(subscriptions);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        public IConsumerSubscription Subscribe(string subject, IObserver<MsgOp> observer, int? unsubAfterNMessages = null)
            => Subscribe(new SubscriptionInfo(subject), observer, unsubAfterNMessages);

        public IConsumerSubscription Subscribe(SubscriptionInfo subscriptionInfo, IObserver<MsgOp> observer, int? unsubAfterNMessages = null)
        {
            ThrowIfDisposed();

            var subscription = CreateSubscription(subscriptionInfo, observer);

            _client.Sub(subscription.SubscriptionInfo);
            if (unsubAfterNMessages.HasValue)
                _client.UnSub(subscription.SubscriptionInfo, unsubAfterNMessages);

            return subscription;
        }

        public Task<IConsumerSubscription> SubscribeAsync(string subject, IObserver<MsgOp> observer, int? unsubAfterNMessages = null)
            => SubscribeAsync(new SubscriptionInfo(subject), observer, unsubAfterNMessages);

        public async Task<IConsumerSubscription> SubscribeAsync(SubscriptionInfo subscriptionInfo, IObserver<MsgOp> observer, int? unsubAfterNMessages = null)
        {
            ThrowIfDisposed();

            var subscription = CreateSubscription(subscriptionInfo, observer);

            await _client.SubAsync(subscription.SubscriptionInfo).ForAwait();
            if (unsubAfterNMessages.HasValue)
                await _client.UnSubAsync(subscription.SubscriptionInfo, unsubAfterNMessages).ForAwait();

            return subscription;
        }

        private ConsumerSubscription CreateSubscription(SubscriptionInfo subscriptionInfo, IObserver<MsgOp> observer)
        {
            var subscription = new ConsumerSubscription(subscriptionInfo, _client.MsgOpStream, observer, info =>
            {
                IConsumerSubscription tmp;
                _subscriptions.TryRemove(info.Id, out tmp);
            });
            if (!_subscriptions.TryAdd(subscription.SubscriptionInfo.Id, subscription))
                throw new NatsException($"Could not create subscription. Id='{subscriptionInfo.Id}'. Subject='{subscriptionInfo.Subject}' QueueGroup='{subscriptionInfo.QueueGroup}'; registrered.");

            return subscription;
        }
    }
}