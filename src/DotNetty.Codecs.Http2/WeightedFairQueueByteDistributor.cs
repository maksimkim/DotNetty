// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs.Http2
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Text;
    using DotNetty.Common.Internal;
    using DotNetty.Common.Utilities;

    /**
     * A {@link StreamByteDistributor} that is sensitive to stream priority and uses
     * <a href="https://en.wikipedia.org/wiki/Weighted_fair_queueing">Weighted Fair Queueing</a> approach for distributing
     * bytes.
     * <p>
     * Inspiration for this distributor was taken from Linux's
     * <a href="https://www.kernel.org/doc/Documentation/scheduler/sched-design-CFS.txt">Completely Fair Scheduler</a>
     * to model the distribution of bytes to simulate an "ideal multi-tasking CPU", but in this case we are simulating
     * an "ideal multi-tasking NIC".
     * <p>
     * Each write operation will use the {@link #allocationQuantum(int)} to know how many more bytes should be allocated
     * relative to the next stream which wants to write. This is to balance fairness while also considering goodput.
     */
    public sealed class WeightedFairQueueByteDistributor : StreamByteDistributor
    {
        /**
     * The initial size of the children map is chosen to be conservative on initial memory allocations under
     * the assumption that most streams will have a small number of children. This choice may be
     * sub-optimal if when children are present there are many children (i.e. a web page which has many
     * dependencies to load).
     *
     * Visible only for testing!
     */
        static readonly int INITIAL_CHILDREN_MAP_SIZE = Math.Max(1, SystemPropertyUtil.GetInt("io.netty.http2.childrenMapSize", 2));

        /**
     * FireFox currently uses 5 streams to establish QoS classes.
     */
        const int DEFAULT_MAX_STATE_ONLY_SIZE = 5;

        readonly Http2ConnectionPropertyKey stateKey;

        /**
     * If there is no Http2Stream object, but we still persist priority information then this is where the state will
     * reside.
     */
        readonly IDictionary<int, State> stateOnlyMap;

        /**
     * This queue will hold streams that are not active and provides the capability to retain priority for streams which
     * have no {@link Http2Stream} object. See {@link StateOnlyComparator} for the priority comparator.
     */
        readonly IPriorityQueue<State> stateOnlyRemovalQueue;
        readonly Http2Connection connection;

        readonly State connectionState;

        /**
     * The minimum number of bytes that we will attempt to allocate to a stream. This is to
     * help improve goodput on a per-stream basis.
     */
        int _allocationQuantum = Http2CodecUtil.DEFAULT_MIN_ALLOCATION_CHUNK;
        readonly int maxStateOnlySize;

        public WeightedFairQueueByteDistributor(Http2Connection connection)
            : this(connection, DEFAULT_MAX_STATE_ONLY_SIZE)
        {
        }

        public WeightedFairQueueByteDistributor(Http2Connection connection, int maxStateOnlySize)
        {
            Contract.Requires(maxStateOnlySize > 0, $"maxStateOnlySize: {maxStateOnlySize} (expected: >0)");

            if (maxStateOnlySize == 0)
            {
                this.stateOnlyMap = EmptyDictionary<int, State>.Instance;
                this.stateOnlyRemovalQueue = EmptyPriorityQueue<State>.Instance;
            }
            else
            {
                this.stateOnlyMap = new Dictionary<int, State>();
                // +2 because we may exceed the limit by 2 if a new dependency has no associated Http2Stream object. We need
                // to create the State objects to put them into the dependency tree, which then impacts priority.
                this.stateOnlyRemovalQueue = new PriorityQueue<State>(StateOnlyComparator.INSTANCE, maxStateOnlySize + 2);
            }

            this.maxStateOnlySize = maxStateOnlySize;

            this.connection = connection;
            this.stateKey = connection.newKey();
            Http2Stream connectionStream = connection.connectionStream();
            connectionStream.setProperty(this.stateKey, this.connectionState = new State(this, connectionStream, 16));

            // Register for notification of new streams.
            connection.addListener(new StateTracker(this));
        }

        public void updateStreamableBytes(StreamByteDistributorContext state)
        {
            this.state(state.stream()).updateStreamableBytes(
                Http2CodecUtil.streamableBytes(state),
                state.hasFrame() && state.windowSize() >= 0);
        }

        public void updateDependencyTree(int childStreamId, int parentStreamId, short weight, bool exclusive)
        {
            State state = this.state(childStreamId);
            if (state == null)
            {
                // If there is no State object that means there is no Http2Stream object and we would have to keep the
                // State object in the stateOnlyMap and stateOnlyRemovalQueue. However if maxStateOnlySize is 0 this means
                // stateOnlyMap and stateOnlyRemovalQueue are empty collections and cannot be modified so we drop the State.
                if (this.maxStateOnlySize == 0)
                {
                    return;
                }

                state = new State(this, childStreamId);
                this.stateOnlyRemovalQueue.TryEnqueue(state);
                this.stateOnlyMap.Add(childStreamId, state);
            }

            State newParent = this.state(parentStreamId);
            if (newParent == null)
            {
                // If there is no State object that means there is no Http2Stream object and we would have to keep the
                // State object in the stateOnlyMap and stateOnlyRemovalQueue. However if maxStateOnlySize is 0 this means
                // stateOnlyMap and stateOnlyRemovalQueue are empty collections and cannot be modified so we drop the State.
                if (this.maxStateOnlySize == 0)
                {
                    return;
                }

                newParent = new State(this, parentStreamId);
                this.stateOnlyRemovalQueue.TryEnqueue(newParent);
                this.stateOnlyMap.Add(parentStreamId, newParent);
                // Only the stream which was just added will change parents. So we only need an array of size 1.
                List<ParentChangedEvent> events = new List<ParentChangedEvent>(1);
                this.connectionState.takeChild(newParent, false, events);
                this.notifyParentChanged(events);
            }

            // if activeCountForTree == 0 then it will not be in its parent's pseudoTimeQueue and thus should not be counted
            // toward parent.totalQueuedWeights.
            if (state.activeCountForTree != 0 && state.parent != null)
            {
                state.parent.totalQueuedWeights += weight - state.weight;
            }

            state.weight = weight;

            if (newParent != state.parent || (exclusive && newParent.children.Count != 1))
            {
                List<ParentChangedEvent> events;
                if (newParent.isDescendantOf(state))
                {
                    events = new List<ParentChangedEvent>(2 + (exclusive ? newParent.children.Count : 0));
                    state.parent.takeChild(newParent, false, events);
                }
                else
                {
                    events = new List<ParentChangedEvent>(1 + (exclusive ? newParent.children.Count : 0));
                }

                newParent.takeChild(state, exclusive, events);
                this.notifyParentChanged(events);
            }

            // The location in the dependency tree impacts the priority in the stateOnlyRemovalQueue map. If we created new
            // State objects we must check if we exceeded the limit after we insert into the dependency tree to ensure the
            // stateOnlyRemovalQueue has been updated.
            while (this.stateOnlyRemovalQueue.Count > this.maxStateOnlySize)
            {
                this.stateOnlyRemovalQueue.TryDequeue(out State stateToRemove);
                stateToRemove.parent.removeChild(stateToRemove);
                this.stateOnlyMap.Remove(stateToRemove.streamId);
            }
        }

        public bool distribute(int maxBytes, Http2StreamWriter writer)
        {
            // As long as there is some active frame we should write at least 1 time.
            if (this.connectionState.activeCountForTree == 0)
            {
                return false;
            }

            // The goal is to write until we write all the allocated bytes or are no longer making progress.
            // We still attempt to write even after the number of allocated bytes has been exhausted to allow empty frames
            // to be sent. Making progress means the active streams rooted at the connection stream has changed.
            int oldIsActiveCountForTree;
            do
            {
                oldIsActiveCountForTree = this.connectionState.activeCountForTree;
                // connectionState will never be active, so go right to its children.
                maxBytes -= this.distributeToChildren(maxBytes, writer, this.connectionState);
            }
            while (this.connectionState.activeCountForTree != 0 && (maxBytes > 0 || oldIsActiveCountForTree != this.connectionState.activeCountForTree));

            return this.connectionState.activeCountForTree != 0;
        }

        /**
         * Sets the amount of bytes that will be allocated to each stream. Defaults to 1KiB.
         * @param allocationQuantum the amount of bytes that will be allocated to each stream. Must be &gt; 0.
         */
        public void allocationQuantum(int allocationQuantum)
        {
            if (allocationQuantum <= 0)
            {
                throw new ArgumentException("allocationQuantum must be > 0");
            }

            this._allocationQuantum = allocationQuantum;
        }

        int distribute(int maxBytes, Http2StreamWriter writer, State state)
        {
            if (state.isActive())
            {
                int nsent = Math.Min(maxBytes, state.streamableBytes);
                state.write(nsent, writer);
                if (nsent == 0 && maxBytes != 0)
                {
                    // If a stream sends zero bytes, then we gave it a chance to write empty frames and it is now
                    // considered inactive until the next call to updateStreamableBytes. This allows descendant streams to
                    // be allocated bytes when the parent stream can't utilize them. This may be as a result of the
                    // stream's flow control window being 0.
                    state.updateStreamableBytes(state.streamableBytes, false);
                }

                return nsent;
            }

            return this.distributeToChildren(maxBytes, writer, state);
        }

        /**
         * It is a pre-condition that {@code state.poll()} returns a non-{@code null} value. This is a result of the way
         * the allocation algorithm is structured and can be explained in the following cases:
         * <h3>For the recursive case</h3>
         * If a stream has no children (in the allocation tree) than that node must be active or it will not be in the
         * allocation tree. If a node is active then it will not delegate to children and recursion ends.
         * <h3>For the initial case</h3>
         * We check connectionState.activeCountForTree == 0 before any allocation is done. So if the connection stream
         * has no active children we don't get into this method.
         */
        int distributeToChildren(int maxBytes, Http2StreamWriter writer, State state)
        {
            long oldTotalQueuedWeights = state.totalQueuedWeights;
            State childState = state.pollPseudoTimeQueue();
            State nextChildState = state.peekPseudoTimeQueue();
            childState.setDistributing();
            try
            {
                Contract.Assert(
                    nextChildState == null || nextChildState.pseudoTimeToWrite >= childState.pseudoTimeToWrite,
                    $"nextChildState[{nextChildState.streamId}].pseudoTime({nextChildState.pseudoTimeToWrite}) <  childState[{childState.streamId}].pseudoTime({childState.pseudoTimeToWrite})");

                int nsent = this.distribute(
                    nextChildState == null
                        ? maxBytes
                        : Math.Min(
                            maxBytes,
                            (int)Math.Min(
                                (nextChildState.pseudoTimeToWrite - childState.pseudoTimeToWrite) * childState.weight / oldTotalQueuedWeights + this._allocationQuantum,
                                int.MaxValue
                            )
                        ),
                    writer,
                    childState);
                state.pseudoTime += nsent;
                childState.updatePseudoTime(state, nsent, oldTotalQueuedWeights);
                return nsent;
            }
            finally
            {
                childState.unsetDistributing();
                // Do in finally to ensure the internal flags is not corrupted if an exception is thrown.
                // The offer operation is delayed until we unroll up the recursive stack, so we don't have to remove from
                // the priority pseudoTimeQueue due to a write operation.
                if (childState.activeCountForTree != 0)
                {
                    state.offerPseudoTimeQueue(childState);
                }
            }
        }

        State state(Http2Stream stream)
        {
            return stream.getProperty<State>(this.stateKey);
        }

        State state(int streamId)
        {
            Http2Stream stream = this.connection.stream(streamId);

            return stream != null ? this.state(stream) : (this.stateOnlyMap.TryGetValue(streamId, out State state) ? state : null);
        }

        /**
         * For testing only!
         */
        bool isChild(int childId, int parentId, short weight)
        {
            State parent = this.state(parentId);
            State child;
            return parent.children.ContainsKey(childId) &&
                (child = this.state(childId)).parent == parent && child.weight == weight;
        }

        /**
         * For testing only!
         */
        int numChildren(int streamId)
        {
            State state = this.state(streamId);
            return state == null ? 0 : state.children.Count;
        }

        /**
         * Notify all listeners of the priority tree change events (in ascending order)
         * @param events The events (top down order) which have changed
         */
        void notifyParentChanged(List<ParentChangedEvent> events)
        {
            for (int i = 0; i < events.Count; ++i)
            {
                ParentChangedEvent evt = events[i];

                this.stateOnlyRemovalQueue.PriorityChanged(evt.state);
                if (evt.state.parent != null && evt.state.activeCountForTree != 0)
                {
                    evt.state.parent.offerAndInitializePseudoTime(evt.state);
                    evt.state.parent.activeCountChangeForTree(evt.state.activeCountForTree);
                }
            }
        }

        class StateTracker : Http2ConnectionAdapter
        {
            readonly WeightedFairQueueByteDistributor parent;

            public StateTracker(WeightedFairQueueByteDistributor parent)
            {
                this.parent = parent;
            }

            public override void onStreamAdded(Http2Stream stream)
            {
                int streamId = stream.id();
                if (!this.parent.stateOnlyMap.TryGetValue(streamId, out State state) || !this.parent.stateOnlyMap.Remove(streamId))
                {
                    state = new State(this.parent, stream);
                    // Only the stream which was just added will change parents. So we only need an array of size 1.
                    List<ParentChangedEvent> events = new List<ParentChangedEvent>(1);
                    this.parent.connectionState.takeChild(state, false, events);
                    this.parent.notifyParentChanged(events);
                }
                else
                {
                    this.parent.stateOnlyRemovalQueue.TryRemove(state);
                    state.stream = stream;
                }

                Http2StreamState streamState = stream.state();
                if (Http2StreamState.RESERVED_REMOTE.Equals(streamState) || Http2StreamState.RESERVED_LOCAL.Equals(streamState))
                {
                    state.setStreamReservedOrActivated();
                    // wasStreamReservedOrActivated is part of the comparator for stateOnlyRemovalQueue there is no
                    // need to reprioritize here because it will not be in stateOnlyRemovalQueue.
                }

                stream.setProperty(this.parent.stateKey, state);
            }

            public override void onStreamActive(Http2Stream stream)
            {
                this.parent.state(stream).setStreamReservedOrActivated();
                // wasStreamReservedOrActivated is part of the comparator for stateOnlyRemovalQueue there is no need to
                // reprioritize here because it will not be in stateOnlyRemovalQueue.
            }

            public override void onStreamClosed(Http2Stream stream)
            {
                this.parent.state(stream).close();
            }

            public override void onStreamRemoved(Http2Stream stream)
            {
                // The stream has been removed from the connection. We can no longer rely on the stream's property
                // storage to track the State. If we have room, and the precedence of the stream is sufficient, we
                // should retain the State in the stateOnlyMap.
                State state = this.parent.state(stream);

                // Typically the stream is set to null when the stream is closed because it is no longer needed to write
                // data. However if the stream was not activated it may not be closed (reserved streams) so we ensure
                // the stream reference is set to null to avoid retaining a reference longer than necessary.
                state.stream = null;

                if (this.parent.maxStateOnlySize == 0)
                {
                    state.parent.removeChild(state);
                    return;
                }

                if (this.parent.stateOnlyRemovalQueue.Count == this.parent.maxStateOnlySize)
                {
                    this.parent.stateOnlyRemovalQueue.TryPeek(out State stateToRemove);
                    if (StateOnlyComparator.INSTANCE.Compare(stateToRemove, state) >= 0)
                    {
                        // The "lowest priority" stream is a "higher priority" than the stream being removed, so we
                        // just discard the state.
                        state.parent.removeChild(state);
                        return;
                    }

                    this.parent.stateOnlyRemovalQueue.TryDequeue(out State _);
                    stateToRemove.parent.removeChild(stateToRemove);
                    this.parent.stateOnlyMap.Remove(stateToRemove.streamId);
                }

                this.parent.stateOnlyRemovalQueue.TryEnqueue(state);
                this.parent.stateOnlyMap.Add(state.streamId, state);
            }
        }

        /**
         * A comparator for {@link State} which has no associated {@link Http2Stream} object. The general precedence is:
         * <ul>
         *     <li>Was a stream activated or reserved (streams only used for priority are higher priority)</li>
         *     <li>Depth in the priority tree (closer to root is higher priority></li>
         *     <li>Stream ID (higher stream ID is higher priority - used for tie breaker)</li>
         * </ul>
         */
        sealed class StateOnlyComparator : IComparer<State>
        {
            internal static readonly StateOnlyComparator INSTANCE = new StateOnlyComparator();

            StateOnlyComparator()
            {
            }

            public int Compare(State o1, State o2)
            {
                // "priority only streams" (which have not been activated) are higher priority than streams used for data.
                bool o1Actived = o1.wasStreamReservedOrActivated();

                if (o1Actived != o2.wasStreamReservedOrActivated())
                {
                    return o1Actived ? -1 : 1;
                }

                // Numerically greater depth is higher priority.
                int x = o2.dependencyTreeDepth - o1.dependencyTreeDepth;

                // I also considered tracking the number of streams which are "activated" (eligible transfer data) at each
                // subtree. This would require a traversal from each node to the root on dependency tree structural changes,
                // and then it would require a re-prioritization at each of these nodes (instead of just the nodes where the
                // direct parent changed). The costs of this are judged to be relatively high compared to the nominal
                // benefit it provides to the heuristic. Instead folks should just increase maxStateOnlySize.

                // Last resort is to give larger stream ids more priority.
                return x != 0 ? x : o1.streamId - o2.streamId;
            }
        }

        sealed class StatePseudoTimeComparator : IComparer<State>
        {
            internal static readonly StatePseudoTimeComparator INSTANCE = new StatePseudoTimeComparator();

            StatePseudoTimeComparator()
            {
            }

            public int Compare(State o1, State o2)
            {
                return MathUtil.Compare(o1.pseudoTimeToWrite, o2.pseudoTimeToWrite);
            }
        }

        /**
         * The remote flow control state for a single stream.
         */
        class State : IPriorityQueueNode<State>
        {
            const int INDEX_NOT_IN_QUEUE = -1;

            static readonly byte STATE_IS_ACTIVE = 0x1;
            static readonly byte STATE_IS_DISTRIBUTING = 0x2;
            static readonly byte STATE_STREAM_ACTIVATED = 0x4;

            /**
             * Maybe {@code null} if the stream if the stream is not active.
             */
            internal Http2Stream stream;
            internal State parent;

            internal IDictionary<int, State> children = EmptyDictionary<int, State>.Instance;

            readonly IPriorityQueue<State> pseudoTimeQueue;
            internal readonly int streamId;
            internal int streamableBytes;

            internal int dependencyTreeDepth;

            /**
             * Count of nodes rooted at this sub tree with {@link #isActive()} equal to {@code true}.
             */
            internal int activeCountForTree;
            int pseudoTimeQueueIndex = INDEX_NOT_IN_QUEUE;

            int stateOnlyQueueIndex = INDEX_NOT_IN_QUEUE;

            /**
             * An estimate of when this node should be given the opportunity to write data.
             */
            internal long pseudoTimeToWrite;

            /**
             * A pseudo time maintained for immediate children to base their {@link #pseudoTimeToWrite} off of.
             */
            internal long pseudoTime;
            internal long totalQueuedWeights;
            byte flags;
            internal short weight = Http2CodecUtil.DEFAULT_PRIORITY_WEIGHT;

            readonly WeightedFairQueueByteDistributor distributor;

            internal State(WeightedFairQueueByteDistributor distributor, int streamId)
                : this(distributor, streamId, null, 0)
            {
            }

            internal State(WeightedFairQueueByteDistributor distributor, Http2Stream stream)
                : this(distributor, stream, 0)
            {
            }

            internal State(WeightedFairQueueByteDistributor distributor, Http2Stream stream, int initialSize)
                : this(distributor, stream.id(), stream, initialSize)
            {
            }

            internal State(WeightedFairQueueByteDistributor distributor, int streamId, Http2Stream stream, int initialSize)
            {
                this.distributor = distributor;
                this.stream = stream;
                this.streamId = streamId;
                this.pseudoTimeQueue = new PriorityQueue<State>(StatePseudoTimeComparator.INSTANCE, initialSize);
            }

            internal bool isDescendantOf(State state)
            {
                State next = this.parent;
                while (next != null)
                {
                    if (next == state)
                    {
                        return true;
                    }

                    next = next.parent;
                }

                return false;
            }

            internal void takeChild(State child, bool exclusive, List<ParentChangedEvent> events)
            {
                this.takeChild(null, child, exclusive, events);
            }

            /**
             * Adds a child to this priority. If exclusive is set, any children of this node are moved to being dependent on
             * the child.
             */
            void takeChild(IEnumerator<KeyValuePair<int, State>> childItr, State child, bool exclusive, List<ParentChangedEvent> events)
            {
                State oldParent = child.parent;

                if (oldParent != this)
                {
                    events.Add(new ParentChangedEvent(child, oldParent));
                    child.setParent(this);

                    // If the childItr is not null we are iterating over the oldParent.children collection and should
                    // use the iterator to remove from the collection to avoid concurrent modification. Otherwise it is
                    // assumed we are not iterating over this collection and it is safe to call remove directly.
                    /*
                    if (childItr != null)
                    {
                        childItr.remove();
                    }
                    else if (oldParent != null)
                    {
                        oldParent.children.Remove(child.streamId);
                    }*/
                    oldParent.children.Remove(child.streamId);

                    // Lazily initialize the children to save object allocations.
                    this.initChildrenIfEmpty();

                    this.children.Add(child.streamId, child);
                    //Contract.Assert(added, "A stream with the same stream ID was already in the child map.");
                }

                if (exclusive && this.children.Count > 0)
                {
                    // If it was requested that this child be the exclusive dependency of this node,
                    // move any previous children to the child node, becoming grand children of this node.
                    IDictionary<int, State> newChildren = this.removeAllChildrenExcept(child);
                    /*IEnumerator<KeyValuePair<int, State>> itr = newChildren.GetEnumerator();
                    while (itr.MoveNext())
                    {
                        child.takeChild(itr, itr.Current.Value, false, events);
                    }*/

                    foreach (var key in newChildren.Keys.ToArray())
                    {
                        child.takeChild(null, newChildren[key], false, events);
                    }
                }
            }

            /**
             * Removes the child priority and moves any of its dependencies to being direct dependencies on this node.
             */
            internal void removeChild(State child)
            {
                if (this.children.Remove(child.streamId))
                {
                    IDictionary<int, State> grandChildren = child.children;
                    List<ParentChangedEvent> events = new List<ParentChangedEvent>(1 + grandChildren.Count);
                    events.Add(new ParentChangedEvent(child, child.parent));
                    child.setParent(null);

                    // Move up any grand children to be directly dependent on this node.
                    /*IEnumerator<KeyValuePair<int, State>> itr = grandChildren.GetEnumerator();
                    while (itr.MoveNext())
                    {
                        takeChild(itr, itr.Current.Value, false, events);
                    }*/

                    foreach (int key in grandChildren.Keys.ToArray())
                    {
                        this.takeChild(null, grandChildren[key], false, events);
                    }

                    this.distributor.notifyParentChanged(events);
                }
            }

            /**
             * Remove all children with the exception of {@code streamToRetain}.
             * This method is intended to be used to support an exclusive priority dependency operation.
             * @return The map of children prior to this operation, excluding {@code streamToRetain} if present.
             */
            IDictionary<int, State> removeAllChildrenExcept(State stateToRetain)
            {
                bool removed = this.children.TryGetValue(stateToRetain.streamId, out stateToRetain) && this.children.Remove(stateToRetain.streamId);
                IDictionary<int, State> prevChildren = this.children;
                // This map should be re-initialized in anticipation for the 1 exclusive child which will be added.
                // It will either be added directly in this method, or after this method is called...but it will be added.
                this.initChildren();
                if (removed)
                {
                    this.children.Add(stateToRetain.streamId, stateToRetain);
                }

                return prevChildren;
            }

            void setParent(State newParent)
            {
                // if activeCountForTree == 0 then it will not be in its parent's pseudoTimeQueue.
                if (this.activeCountForTree != 0 && this.parent != null)
                {
                    this.parent.removePseudoTimeQueue(this);
                    this.parent.activeCountChangeForTree(-1 * this.activeCountForTree);
                }

                this.parent = newParent;
                // Use MAX_VALUE if no parent because lower depth is considered higher priority by StateOnlyComparator.
                this.dependencyTreeDepth = newParent == null ? int.MaxValue : newParent.dependencyTreeDepth + 1;
            }

            void initChildrenIfEmpty()
            {
                if (this.children == EmptyDictionary<int, State>.Instance)
                {
                    this.initChildren();
                }
            }

            void initChildren()
            {
                //children = new ConcurrentDictionary<int, State>(WeightedFairQueueByteDistributor.INITIAL_CHILDREN_MAP_SIZE);
                this.children = new Dictionary<int, State>();
            }

            internal void write(int numBytes, Http2StreamWriter writer)
            {
                Contract.Assert(this.stream != null);
                try
                {
                    writer.write(this.stream, numBytes);
                }
                catch (Exception t)
                {
                    throw Http2Exception.connectionError(Http2Error.INTERNAL_ERROR, t, "byte distribution write error");
                }
            }

            internal void activeCountChangeForTree(int increment)
            {
                Contract.Assert(this.activeCountForTree + increment >= 0);
                this.activeCountForTree += increment;
                if (this.parent != null)
                {
                    Contract.Assert(
                        this.activeCountForTree != increment || this.pseudoTimeQueueIndex == INDEX_NOT_IN_QUEUE || this.parent.pseudoTimeQueue.Contains(this),
                        $"State[{this.streamId}].activeCountForTree changed from 0 to {increment} is in a pseudoTimeQueue, but not in parent[{this.parent.streamId}]'s pseudoTimeQueue");
                    if (this.activeCountForTree == 0)
                    {
                        this.parent.removePseudoTimeQueue(this);
                    }
                    else if (this.activeCountForTree == increment && !this.isDistributing())
                    {
                        // If frame count was 0 but is now not, and this node is not already in a pseudoTimeQueue (assumed
                        // to be pState's pseudoTimeQueue) then enqueue it. If this State object is being processed the
                        // pseudoTime for this node should not be adjusted, and the node will be added back to the
                        // pseudoTimeQueue/tree structure after it is done being processed. This may happen if the
                        // activeCountForTree == 0 (a node which can't stream anything and is blocked) is at/near root of
                        // the tree, and is popped off the pseudoTimeQueue during processing, and then put back on the
                        // pseudoTimeQueue because a child changes position in the priority tree (or is closed because it is
                        // not blocked and finished writing all data).
                        this.parent.offerAndInitializePseudoTime(this);
                    }

                    this.parent.activeCountChangeForTree(increment);
                }
            }

            internal void updateStreamableBytes(int newStreamableBytes, bool isActive)
            {
                if (this.isActive() != isActive)
                {
                    if (isActive)
                    {
                        this.activeCountChangeForTree(1);
                        this.setActive();
                    }
                    else
                    {
                        this.activeCountChangeForTree(-1);
                        this.unsetActive();
                    }
                }

                this.streamableBytes = newStreamableBytes;
            }

            /**
             * Assumes the parents {@link #totalQueuedWeights} includes this node's weight.
             */
            internal void updatePseudoTime(State parentState, int nsent, long totalQueuedWeights)
            {
                Contract.Assert(this.streamId != Http2CodecUtil.CONNECTION_STREAM_ID && nsent >= 0);
                // If the current pseudoTimeToSend is greater than parentState.pseudoTime then we previously over accounted
                // and should use parentState.pseudoTime.
                this.pseudoTimeToWrite = Math.Min(this.pseudoTimeToWrite, parentState.pseudoTime) + nsent * totalQueuedWeights / this.weight;
            }

            /**
             * The concept of pseudoTime can be influenced by priority tree manipulations or if a stream goes from "active"
             * to "non-active". This method accounts for that by initializing the {@link #pseudoTimeToWrite} for
             * {@code state} to {@link #pseudoTime} of this node and then calls {@link #offerPseudoTimeQueue(State)}.
             */
            internal void offerAndInitializePseudoTime(State state)
            {
                state.pseudoTimeToWrite = this.pseudoTime;
                this.offerPseudoTimeQueue(state);
            }

            internal void offerPseudoTimeQueue(State state)
            {
                this.pseudoTimeQueue.TryEnqueue(state);
                this.totalQueuedWeights += state.weight;
            }

            /**
             * Must only be called if the pseudoTimeQueue is non-empty!
             */
            internal State pollPseudoTimeQueue()
            {
                this.pseudoTimeQueue.TryDequeue(out State state);
                // This method is only ever called if the pseudoTimeQueue is non-empty.
                this.totalQueuedWeights -= state.weight;
                return state;
            }

            void removePseudoTimeQueue(State state)
            {
                if (this.pseudoTimeQueue.TryRemove(state))
                {
                    this.totalQueuedWeights -= state.weight;
                }
            }

            internal State peekPseudoTimeQueue()
            {
                return this.pseudoTimeQueue.TryPeek(out State result) ? result : null;
            }

            internal void close()
            {
                this.updateStreamableBytes(0, false);
                this.stream = null;
            }

            internal bool wasStreamReservedOrActivated()
            {
                return (this.flags & STATE_STREAM_ACTIVATED) != 0;
            }

            internal void setStreamReservedOrActivated()
            {
                this.flags |= STATE_STREAM_ACTIVATED;
            }

            internal bool isActive()
            {
                return (this.flags & STATE_IS_ACTIVE) != 0;
            }

            void setActive()
            {
                this.flags |= STATE_IS_ACTIVE;
            }

            void unsetActive()
            {
                this.flags &= (byte)~STATE_IS_ACTIVE;
            }

            bool isDistributing()
            {
                return (this.flags & STATE_IS_DISTRIBUTING) != 0;
            }

            internal void setDistributing()
            {
                this.flags |= STATE_IS_DISTRIBUTING;
            }

            internal void unsetDistributing()
            {
                this.flags &= (byte)~STATE_IS_DISTRIBUTING;
            }

            public int GetPriorityQueueIndex(IPriorityQueue<State> queue)
            {
                return queue == this.distributor.stateOnlyRemovalQueue ? this.stateOnlyQueueIndex : this.pseudoTimeQueueIndex;
            }

            public void SetPriorityQueueIndex(IPriorityQueue<State> queue, int i)
            {
                if (queue == this.distributor.stateOnlyRemovalQueue)
                {
                    this.stateOnlyQueueIndex = i;
                }
                else
                {
                    this.pseudoTimeQueueIndex = i;
                }
            }

            public override string ToString()
            {
                // Use activeCountForTree as a rough estimate for how many nodes are in this subtree.
                StringBuilder sb = new StringBuilder(256 * (this.activeCountForTree > 0 ? this.activeCountForTree : 1));
                this.toString(sb);
                return sb.ToString();
            }

            void toString(StringBuilder sb)
            {
                sb.Append("{streamId ").Append(this.streamId)
                    .Append(" streamableBytes ").Append(this.streamableBytes)
                    .Append(" activeCountForTree ").Append(this.activeCountForTree)
                    .Append(" pseudoTimeQueueIndex ").Append(this.pseudoTimeQueueIndex)
                    .Append(" pseudoTimeToWrite ").Append(this.pseudoTimeToWrite)
                    .Append(" pseudoTime ").Append(this.pseudoTime)
                    .Append(" flags ").Append(this.flags)
                    .Append(" pseudoTimeQueue.Count ").Append(this.pseudoTimeQueue.Count)
                    .Append(" stateOnlyQueueIndex ").Append(this.stateOnlyQueueIndex)
                    .Append(" parent.streamId ").Append(this.parent == null ? -1 : this.parent.streamId).Append("} [");

                if (this.pseudoTimeQueue.Count > 0)
                {
                    foreach (var s in this.pseudoTimeQueue)
                    {
                        s.toString(sb);
                        sb.Append(", ");
                    }

                    // Remove the last ", "
                    sb.Length = sb.Length - 2;
                }

                sb.Append(']');
            }
        }

        /**
         * Allows a correlation to be made between a stream and its old parent before a parent change occurs.
         */

        //struct?
        sealed class ParentChangedEvent
        {
            internal readonly State state;
            readonly State oldParent;

            /**
             * Create a new instance.
             * @param state The state who has had a parent change.
             * @param oldParent The previous parent.
             */
            internal ParentChangedEvent(State state, State oldParent)
            {
                this.state = state;
                this.oldParent = oldParent;
            }
        }
    }
}