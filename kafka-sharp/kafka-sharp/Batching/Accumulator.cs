﻿// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Kafka.Common;

namespace Kafka.Batching
{
    #region base Accumulator

    /// <summary>
    /// Accumulate some data until we reach a given count or a period of time has elapsed.
    /// This class factorizes count/timer logic.
    /// </summary>
    /// <typeparam name="TData"></typeparam>
    abstract class Accumulator<TData>  : IDisposable
    {
        private readonly object _lock = new object();
        private readonly TimeSpan _timeWindow;
        private readonly int _limit;
        private bool _disposed;
        private int _count;
        private long _window;
        private Timer _timer;

        protected abstract void OnNewBatch(int count);
        protected abstract void Accumulate(TData data);

        protected Accumulator(int maxCount, TimeSpan timeWindow)
        {
            _limit = maxCount;
            _timeWindow = timeWindow;
            Start(_window);
        }

        private void Start(long window)
        {
            _timer = new Timer(_ => Tick(window), null, _timeWindow, TimeSpan.FromMilliseconds(-1));
        }

        private void SignalNewBatch()
        {
            ++_window;
            if (_count > 0)
            {
                OnNewBatch(_count);
            }
            _count = 0;
            _timer.Dispose();
            Start(_window);
        }

        private void Tick(long id)
        {
            lock (_lock)
            {
                if (id != _window || _disposed)
                {
                    // Either some race occurred or we're done
                    return;
                }

                SignalNewBatch();
            }
        }

        public bool Add(TData data)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }
                Accumulate(data);
                if (++_count >= _limit)
                {
                    SignalNewBatch();
                }
            }
            return true;
        }

        #region IDisposable Members

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                SignalNewBatch();
                _timer.Dispose();
                _timer = null;
            }
        }

        #endregion
    }

    #endregion

    #region Accumulator by topic

    interface IBatchByTopic<out TData> : IEnumerable<IGrouping<string, TData>>, IDisposable
    {
        int Count { get; }
    }

    /// <summary>
    /// Encapsulate a bunch of data keyed by topic.
    /// </summary>
    /// <typeparam name="TData"></typeparam>
    class BatchByTopic<TData> : IBatchByTopic<TData>
    {
        private static readonly Pool<BatchByTopic<TData>> _pool = new Pool<BatchByTopic<TData>>(
            () => new BatchByTopic<TData>(),
            b =>
            {
                foreach (var value in b._batch.Values)
                {
                    value.Dispose();
                }
                b._batch.Clear();
                b.Count = 0;
            });

        private readonly Dictionary<string, Grouping<string, TData>> _batch = new Dictionary<string, Grouping<string, TData>>();

        public static BatchByTopic<TData> New()
        {
            return _pool.Reserve();
        }

        protected BatchByTopic()
        {
        }

        public int Count { get; private set; }

        public void Add(string topic, TData data)
        {
            Grouping<string, TData> grouping;
            if (!_batch.TryGetValue(topic, out grouping))
            {
                grouping = Grouping<string, TData>.New(topic);
                _batch[topic] = grouping;
            }
            grouping.Add(data);
            ++Count;
        }

        #region IEnumerable<IGrouping<string,TData>> Members

        public IEnumerator<IGrouping<string, TData>> GetEnumerator()
        {
            return _batch.Values.GetEnumerator();
        }

        #endregion

        #region IEnumerable Members

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return _batch.Values.GetEnumerator();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _pool.Release(this);
        }

        #endregion
    }

    /// <summary>
    /// Accumulate some data and present it "by topic".
    /// </summary>
    /// <typeparam name="TData"></typeparam>
    class AccumulatorByTopic<TData> : Accumulator<TData>
    {
        private readonly Func<TData, string> _topicFromData;
        private BatchByTopic<TData> _currentBatch;

        public event Action<IBatchByTopic<TData>> NewBatch = _ => {};

        public AccumulatorByTopic(Func<TData, string> topicFromData, int maxCount, TimeSpan timeWindow) : base(maxCount, timeWindow)
        {
            _topicFromData = topicFromData;
            _currentBatch = BatchByTopic<TData>.New();
        }

        protected override void OnNewBatch(int count)
        {
            NewBatch(_currentBatch);
            _currentBatch = BatchByTopic<TData>.New();
        }

        protected override void Accumulate(TData data)
        {
            _currentBatch.Add(_topicFromData(data), data);
        }
    }

    #endregion

    #region Accumulator by topic by partition

    interface IBatchByTopicByPartition<out TData>
        : IEnumerable<IGrouping<string, IGrouping<int, TData>>>, IDisposable
    {
        int Count { get; }
    }

    class BatchByTopicByPartition<TData> : IBatchByTopicByPartition<TData>
    {
        private static readonly Pool<BatchByTopicByPartition<TData>> _pool = new Pool<BatchByTopicByPartition<TData>>(
            () => new BatchByTopicByPartition<TData>(),
            b =>
            {
                foreach (var byTopic in b._batch.Values)
                {
                    byTopic.Dispose();
                }
                b._batch.Clear();
                b.Count = 0;
            });

        private readonly Dictionary<string, Grouping<string, int, TData>> _batch =
            new Dictionary<string, Grouping<string, int, TData>>();

        private BatchByTopicByPartition()
        {
        }

        public static BatchByTopicByPartition<TData> New()
        {
            return _pool.Reserve();
        }

        public int Count { get; private set; }

        public void Add(string topic, int partition, TData data)
        {
            Grouping<string, int, TData> grouping;
            if (!_batch.TryGetValue(topic, out grouping))
            {
                grouping = Grouping<string, int, TData>.New(topic);
                _batch[topic] = grouping;
            }
            grouping.Add(partition, data);
            ++Count;
        }

        #region IEnumerable<IGrouping<string,IEnumerable<IGrouping<int,TData>>>> Members

        public IEnumerator<IGrouping<string, IGrouping<int, TData>>> GetEnumerator()
        {
            return _batch.Values.GetEnumerator();
        }

        #endregion

        #region IEnumerable Members

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return _batch.Values.GetEnumerator();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _pool.Release(this);
        }

        #endregion
    }

    class AccumulatorByTopicByPartition<TData> : Accumulator<TData>
    {
        private readonly Func<TData, string> _topicFromData;
        private readonly Func<TData, int> _partitionFromData;

        private BatchByTopicByPartition<TData> _currentBatch;

        public event Action<IBatchByTopicByPartition<TData>> NewBatch = _ => { };

        public AccumulatorByTopicByPartition(Func<TData, string> topicFromData, Func<TData, int> partitionFromData, int maxCount, TimeSpan timeWindow)
            : base(maxCount, timeWindow)
        {
            _topicFromData = topicFromData;
            _partitionFromData = partitionFromData;
            _currentBatch = BatchByTopicByPartition<TData>.New();
        }

        protected override void OnNewBatch(int count)
        {
            NewBatch(_currentBatch);
            _currentBatch = BatchByTopicByPartition<TData>.New();
        }

        protected override void Accumulate(TData data)
        {
            _currentBatch.Add(_topicFromData(data), _partitionFromData(data), data);
        }
    }

    #endregion
}