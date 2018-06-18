﻿// Copyright 2012-2018 Chris Patterson
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the License for the
// specific language governing permissions and limitations under the License.
namespace GreenPipes.Caching.Internals
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;


    public class NodeTracker<TValue> :
        INodeTracker<TValue>
        where TValue : class
    {
        const double MaxAgeUpperLimit = 24.0 * 60 * 60 * 1000;
        readonly int _bucketCount;
        readonly int _bucketSize;
        readonly object _lock = new object();
        readonly TimeSpan _maxAge;
        readonly TimeSpan _minAge;
        readonly CurrentTimeProvider _nowProvider;
        readonly CacheValueObservable<TValue> _observers;
        readonly TimeSpan _validityCheckInterval;
        BucketCollection<TValue> _buckets;
        DateTime _cacheResetTime;
        bool _cleanupScheduled;
        Bucket<TValue> _currentBucket;
        int _currentBucketIndex;
        DateTime _nextValidityCheck;
        int _oldestBucketIndex;

        public NodeTracker(CacheSettings settings)
        {
            _nowProvider = settings.NowProvider;

            _observers = new CacheValueObservable<TValue>();

            var maxAgeInMilliseconds = Math.Min(settings.MaxAge.TotalMilliseconds, MaxAgeUpperLimit);

            _minAge = settings.MinAge;
            _maxAge = TimeSpan.FromMilliseconds(maxAgeInMilliseconds);

            _validityCheckInterval = TimeSpan.FromMilliseconds(maxAgeInMilliseconds / 240.0);
            _cacheResetTime = _nowProvider().Add(TimeSpan.FromMilliseconds(MaxAgeUpperLimit));

            _bucketSize = Math.Max(settings.Capacity / settings.BucketCount, 1);

            // enough buckets for all of the content within the time slices, plus a few spares
            _bucketCount = 240 * settings.BucketCount + 5;

            _buckets = new BucketCollection<TValue>(this, _bucketCount);

            Statistics = new CacheStatistics(settings.Capacity, _bucketCount, _bucketSize, _minAge, _maxAge, _validityCheckInterval);

            OpenBucket(0);
        }

        int OldestBucketIndex
        {
            get => _oldestBucketIndex;
            set
            {
                _oldestBucketIndex = value;
                Statistics.SetBucketIndices(_oldestBucketIndex, _currentBucketIndex);
            }
        }

        int CurrentBucketIndex
        {
            get => _currentBucketIndex;
            set
            {
                _currentBucketIndex = value;
                Statistics.SetBucketIndices(_oldestBucketIndex, _currentBucketIndex);
            }
        }

        bool AreLowOnBuckets => CurrentBucketIndex - OldestBucketIndex > _buckets.Count - 5;

        bool IsCurrentBucketOldest => OldestBucketIndex == CurrentBucketIndex;

        public CacheStatistics Statistics { get; }

        public void Add(INodeValueFactory<TValue> nodeValueFactory)
        {
            Task.Run(() => AddNode(nodeValueFactory));
        }

        public void Add(TValue value)
        {
            AddValue(value);
        }

        public void Remove(INode<TValue> node)
        {
            Task.Run(() => RemoveNode(node));
        }

        public IEnumerable<INode<TValue>> GetAll()
        {
            for (var bucketIndex = CurrentBucketIndex; bucketIndex >= OldestBucketIndex; --bucketIndex)
            {
                Bucket<TValue> bucket = _buckets[bucketIndex];

                // this is weird, but once a bucket starts being emptied, we want to stop walking the list since
                // it's a total race condition - so check if the bucket has a First, and if it doesn't, we're done
                // with this bucket. Prevents having to take a lock on it.
                for (IBucketNode<TValue> node = bucket.Head; node != null && bucket.Head != null; node = node.Next)
                {
                    if (node.IsValid)
                        yield return node;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _buckets.Empty();

                _buckets = new BucketCollection<TValue>(this, _bucketCount);

                _cacheResetTime = _nowProvider().Add(TimeSpan.FromMilliseconds(MaxAgeUpperLimit));

                OldestBucketIndex = 0;

                OpenBucket(0);

                Statistics.Reset();

                _observers.CacheCleared();
            }
        }

        public void Rebucket(IBucketNode<TValue> node)
        {
            lock (_lock)
            {
                node.AssignToBucket(_currentBucket);
            }
        }

        public ConnectHandle Connect(ICacheValueObserver<TValue> observer)
        {
            return _observers.Connect(observer);
        }

        bool IsCleanupRequired(DateTime now) => (_currentBucket.Count > _bucketSize || now > _nextValidityCheck) && _cleanupScheduled == false;

        async Task AddNode(INodeValueFactory<TValue> nodeValueFactory)
        {
            try
            {
                var value = await nodeValueFactory.CreateValue().ConfigureAwait(false);

                Statistics.Miss();

                AddValue(value);
            }
            catch (Exception)
            {
                Statistics.CreateFaulted();
            }
        }

        void AddValue(TValue value)
        {
            var node = new BucketNode<TValue>(value);

            lock (_lock)
            {
                _currentBucket.Push(node);

                Statistics.ValueAdded();

                CheckCacheStatus();
            }

            _observers.ValueAdded(node, value);
        }

        async Task RemoveNode(INode<TValue> node)
        {
            try
            {
                var value = await node.Value.ConfigureAwait(false);

                Statistics.ValueRemoved();

                CheckCacheStatus();

                _observers.ValueRemoved(node, value);

                switch (value)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                //
            }
        }

        void OpenBucket(int index)
        {
            var lockTaken = false;

            Monitor.Enter(_lock, ref lockTaken);
            try
            {
                var now = _nowProvider();

                if (_currentBucket != null)
                {
                    lock (_currentBucket)
                    {
                        _currentBucket.Stop(now);
                    }
                }

                CurrentBucketIndex = index;

                Bucket<TValue> openingBucket = _buckets[index];
                openingBucket.Start(now);

                _currentBucket = openingBucket;

                _nextValidityCheck = now.Add(_validityCheckInterval);
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(_lock);
            }
        }

        void CheckCacheStatus()
        {
            if (Monitor.TryEnter(_lock))
            {
                try
                {
                    var now = _nowProvider();

                    if (!IsCleanupRequired(now))
                        return;

                    if (CurrentBucketIndex > 1000000000 || now >= _cacheResetTime)
                        Clear();
                    else
                    {
                        Volatile.Write(ref _cleanupScheduled, true);

                        Task.Run(() => Cleanup(now));
                    }
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        async Task Cleanup(DateTime now)
        {
            if (Monitor.TryEnter(_lock))
            {
                try
                {
                    var itemsAboveCapacity = Statistics.Count - Statistics.Capacity;
                    Bucket<TValue> bucket = _buckets[OldestBucketIndex];

                    var expiration = now - _maxAge;
                    var aged = now - _minAge;
                    while (AreLowOnBuckets
                        || bucket.HasExpired(expiration)
                        || itemsAboveCapacity > 0 && bucket.IsOldEnough(aged))
                    {
                        IBucketNode<TValue> node = bucket.Head;

                        bucket.Clear();

                        while (node != null)
                        {
                            IBucketNode<TValue> next = node.Pop();

                            if (node.IsValid && node.Bucket != null)
                            {
                                // if the node is in its original bucket, it's ripe for the pickin
                                if (node.Bucket == bucket)
                                {
                                    --itemsAboveCapacity;

                                    await EvictNode(node).ConfigureAwait(false);
                                }
                                else
                                {
                                    // push it onto the bucket that now contains it
                                    node.Bucket.Push(node);
                                }
                            }

                            node = next;
                        }

                        bucket = _buckets[++OldestBucketIndex];

                        if (IsCurrentBucketOldest)
                            break;
                    }

                    OpenBucket(++CurrentBucketIndex);
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }

            _cleanupScheduled = false;
        }

        async Task EvictNode(IBucketNode<TValue> node)
        {
            var value = await node.Value.ConfigureAwait(false);

            Statistics.ValueRemoved();

            _observers.ValueRemoved(node, value);

            node.Evict();

            try
            {
                switch (value)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                //
            }
        }
    }
}