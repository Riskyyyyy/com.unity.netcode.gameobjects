using System;
using System.Collections.Generic;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Messaging;
using MLAPI.NetworkVariable;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;
using UnityEngine;
using UnityEngine.UIElements;

namespace MLAPI
{
    // Structure that acts as a key for a NetworkVariable
    // Allows telling which variable we're talking about.
    // Might include tick in a future milestone, to address past variable value
    internal struct VariableKey
    {
        public ulong NetworkObjectId; // the NetworkObjectId of the owning GameObject
        public ushort BehaviourIndex; // the index of the behaviour in this GameObject
        public ushort VariableIndex; // the index of the variable in this NetworkBehaviour
        public ushort TickWritten; // the network tick at which this variable was set
    }

    // Index for a NetworkVariable in our table of variables
    // Store when a variable was written and where the variable is serialized
    internal struct Entry
    {
        public VariableKey Key;
        public ushort Position; // the offset in our Buffer
        public ushort Length; // the length of the data in Buffer
        public bool Fresh; // indicates entries that were just received

        public const int NotFound = -1;
    }

    // A table of NetworkVariables that constitutes a Snapshot.
    // Stores serialized NetworkVariables
    // todo --M1--
    // The Snapshot will change for M1b with memory management, instead of just FreeMemoryPosition, there will be data structure
    // around available buffer, etc.
    internal class Snapshot
    {
        // todo --M1-- functionality to grow these will be needed in a later milestone
        private const int k_MaxVariables = 64;
        private const int k_BufferSize = 20000;

        public byte[] Buffer = new byte[k_BufferSize];
        internal IndexAllocator m_Allocator = new IndexAllocator(20000);

        public Entry[] Entries = new Entry[k_MaxVariables];
        public int LastEntry = 0;
        public MemoryStream Stream;

        private NetworkManager m_NetworkManager;
        private bool m_tickIndex;

        /// <summary>
        /// Constructor
        /// Allocated a MemoryStream to be reused for this Snapshot
        /// </summary>
        /// <param name="networkManager">The NetworkManaher this Snapshot uses. Needed upon receive to set Variables</param>
        /// <param name="tickIndex">Whether this Snapshot uses the tick as an index</param>
        public Snapshot(NetworkManager networkManager, bool tickIndex)
        {
            Stream = new MemoryStream(Buffer, 0, k_BufferSize);
            m_NetworkManager = networkManager;
            m_tickIndex = tickIndex;
        }

        // todo --M1--
        // Find will change to be efficient in a future milestone
        /// <summary>
        /// Finds the position of a given NetworkVariable, given its key
        /// </summary>
        /// <param name="key">The key we're looking for</param>
        public int Find(VariableKey key)
        {
            for (int i = 0; i < LastEntry; i++)
            {
                if (Entries[i].Key.NetworkObjectId == key.NetworkObjectId &&
                    Entries[i].Key.BehaviourIndex == key.BehaviourIndex &&
                    Entries[i].Key.VariableIndex == key.VariableIndex &&
                    (!m_tickIndex || (Entries[i].Key.TickWritten == key.TickWritten)))
                {
                    return i;
                }
            }

            return Entry.NotFound;
        }

        /// <summary>
        /// Adds an entry in the table for a new key
        /// </summary>
        public int AddEntry(VariableKey k)
        {
            var pos = LastEntry++;
            var entry = Entries[pos];

            entry.Key = k;
            entry.Position = 0;
            entry.Length = 0;
            entry.Fresh = false;
            Entries[pos] = entry;

            return pos;
        }

        /// <summary>
        /// Write an Entry to send
        /// Must match ReadEntry
        /// </summary>
        /// <param name="writer">The writer to write the entry to</param>
        internal void WriteEntry(NetworkWriter writer, in Entry entry)
        {
            writer.WriteUInt64(entry.Key.NetworkObjectId);
            writer.WriteUInt16(entry.Key.BehaviourIndex);
            writer.WriteUInt16(entry.Key.VariableIndex);
            writer.WriteUInt16(entry.Key.TickWritten);
            writer.WriteUInt16(entry.Position);
            writer.WriteUInt16(entry.Length);
        }

        /// <summary>
        /// Read a received Entry
        /// Must match WriteEntry
        /// </summary>
        /// <param name="reader">The readed to read the entry from</param>
        internal Entry ReadEntry(NetworkReader reader)
        {
            Entry entry;
            entry.Key.NetworkObjectId = reader.ReadUInt64();
            entry.Key.BehaviourIndex = reader.ReadUInt16();
            entry.Key.VariableIndex = reader.ReadUInt16();
            entry.Key.TickWritten = reader.ReadUInt16();
            entry.Position = reader.ReadUInt16();
            entry.Length = reader.ReadUInt16();
            entry.Fresh = false;

            return entry;
        }

        /// <summary>
        /// Allocate memory from the buffer for the Entry and update it to point to the right location
        /// </summary>
        /// <param name="entry">The entry to allocate for</param>
        /// <param name="size">The need size in bytes</param>
        public void AllocateEntry(ref Entry entry, int index, int size)
        {
            // todo --M1--
            // this will change once we start reusing the snapshot buffer memory
            // todo: deal with free space
            // todo: deal with full buffer

            int pos;

            if (entry.Length > 0)
            {
                m_Allocator.Deallocate(index);
            }

            bool ret = m_Allocator.Allocate(index, size, out pos);

            if (!ret)
            {
                //todo: error handling
            }

            entry.Position = (ushort)pos;
            entry.Length = (ushort)size;
        }

        /// <summary>
        /// Read the buffer part of a snapshot
        /// Must match WriteBuffer
        /// The stream is actually a memory stream and we seek to each variable position as we deserialize them
        /// </summary>
        /// <param name="reader">The NetworkReader to read our buffer of variables from</param>
        /// <param name="snapshotStream">The stream to read our buffer of variables from</param>
        internal void ReadBuffer(NetworkReader reader, Stream snapshotStream)
        {
            int snapshotSize = reader.ReadUInt16();

            snapshotStream.Read(Buffer, 0, snapshotSize);

            for (var i = 0; i < LastEntry; i++)
            {
                if (Entries[i].Fresh && Entries[i].Key.TickWritten > 0)
                {
                    var nv = FindNetworkVar(Entries[i].Key);

                    Stream.Seek(Entries[i].Position, SeekOrigin.Begin);

                    // todo --M1--
                    // Review whether tick still belong in netvar or in the snapshot table.
                    nv.ReadDelta(Stream, m_NetworkManager.IsServer, m_NetworkManager.NetworkTickSystem.GetTick(), Entries[i].Key.TickWritten);
                }

                Entries[i].Fresh = false;
            }
        }

        /// <summary>
        /// Read the snapshot index from a buffer
        /// Stores the entry. Allocates memory if needed. The actual buffer will be read later
        /// </summary>
        /// <param name="reader">The reader to read the index from</param>
        internal void ReadIndex(NetworkReader reader)
        {
            Entry entry;
            short entries = reader.ReadInt16();

            for (var i = 0; i < entries; i++)
            {
                entry = ReadEntry(reader);
                entry.Fresh = true;

                int pos = Find(entry.Key);
                if (pos == Entry.NotFound)
                {
                    pos = AddEntry(entry.Key);
                }

                // if we need to allocate more memory (the variable grew in size)
                if (Entries[pos].Length < entry.Length)
                {
                    AllocateEntry(ref entry, pos, entry.Length);
                }

                Entries[pos] = entry;
            }
        }

        /// <summary>
        /// Helper function to find the NetworkVariable object from a key
        /// This will look into all spawned objects
        /// </summary>
        /// <param name="key">The key to search for</param>
        private INetworkVariable FindNetworkVar(VariableKey key)
        {
            var spawnedObjects = m_NetworkManager.SpawnManager.SpawnedObjects;

            if (spawnedObjects.ContainsKey(key.NetworkObjectId))
            {
                var behaviour = spawnedObjects[key.NetworkObjectId]
                    .GetNetworkBehaviourAtOrderIndex(key.BehaviourIndex);
                return behaviour.NetworkVariableFields[key.VariableIndex];
            }

            return null;
        }
    }

    public class SnapshotSystem : INetworkUpdateSystem, IDisposable
    {
        private NetworkManager m_NetworkManager = NetworkManager.Singleton;
        private Snapshot m_Snapshot = new Snapshot(NetworkManager.Singleton, false);
        private Dictionary<ulong, Snapshot> m_ClientReceivedSnapshot = new Dictionary<ulong, Snapshot>();

        private ushort m_CurrentTick = 0;

        /// <summary>
        /// Constructor
        /// </summary>
        /// Registers the snapshot system for early updates
        public SnapshotSystem()
        {
            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        /// Unregisters the snapshot system from early updates
        public void Dispose()
        {
            this.UnregisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            if (updateStage == NetworkUpdateStage.EarlyUpdate)
            {
                var tick = m_NetworkManager.NetworkTickSystem.GetTick();

                if (tick != m_CurrentTick)
                {
                    m_CurrentTick = tick;
                    if (m_NetworkManager.IsServer)
                    {
                        for (int i = 0; i < m_NetworkManager.ConnectedClientsList.Count; i++)
                        {
                            var clientId = m_NetworkManager.ConnectedClientsList[i].ClientId;
                            SendSnapshot(clientId);
                        }
                    }
                    else
                    {
                        SendSnapshot(m_NetworkManager.ServerClientId);
                    }

                    //m_Snapshot.m_Allocator.DebugDisplay();
                    /*DebugDisplayStore(m_Snapshot, "Entries");

                    foreach(var item in m_ClientReceivedSnapshot)
                    {
                        DebugDisplayStore(item.Value, "Received Entries " + item.Key);
                    }*/
                }
            }
        }

        // todo --M1--
        // for now, the full snapshot is always sent
        // this will change significantly
        /// <summary>
        /// Send the snapshot to a specific client
        /// </summary>
        /// <param name="clientId">The client index to send to</param>
        private void SendSnapshot(ulong clientId)
        {
            // Send the entry index and the buffer where the variables are serialized
            using (var buffer = PooledNetworkBuffer.Get())
            {
                using (var writer = PooledNetworkWriter.Get(buffer))
                {
                    writer.WriteUInt16(m_CurrentTick);
                }

                WriteIndex(buffer);
                WriteBuffer(buffer);

                m_NetworkManager.MessageSender.Send(clientId, NetworkConstants.SNAPSHOT_DATA,
                    NetworkChannel.SnapshotExchange, buffer);
                buffer.Dispose();
            }
        }

        /// <summary>
        /// Write the snapshot index to a buffer
        /// </summary>
        /// <param name="buffer">The buffer to write the index to</param>
        private void WriteIndex(NetworkBuffer buffer)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteInt16((short)m_Snapshot.LastEntry);
                for (var i = 0; i < m_Snapshot.LastEntry; i++)
                {
                    m_Snapshot.WriteEntry(writer, in m_Snapshot.Entries[i]);
                }
            }
        }

        /// <summary>
        /// Write the buffer of a snapshot
        /// Must match ReadBuffer
        /// </summary>
        /// <param name="buffer">The NetworkBuffer to write our buffer of variables to</param>
        private void WriteBuffer(NetworkBuffer buffer)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteUInt16((ushort)m_Snapshot.m_Allocator.Range);
            }

            // todo --M1--
            // // this sends the whole buffer
            // we'll need to build a per-client list
            buffer.Write(m_Snapshot.Buffer, 0, m_Snapshot.m_Allocator.Range);
        }

        // todo: consider using a Key, instead of 3 ints, if it can be exposed
        /// <summary>
        /// Called by the rest of MLAPI when a NetworkVariable changed and need to go in our snapshot
        /// Might not happen for all variable on every frame. Might even happen more than once.
        /// </summary>
        /// <param name="networkVariable">The NetworkVariable to write, or rather, its INetworkVariable</param>
        public void Store(ulong networkObjectId, int behaviourIndex, int variableIndex, INetworkVariable networkVariable)
        {
            VariableKey k;
            k.NetworkObjectId = networkObjectId;
            k.BehaviourIndex = (ushort)behaviourIndex;
            k.VariableIndex = (ushort)variableIndex;
            k.TickWritten = m_NetworkManager.NetworkTickSystem.GetTick();

            int pos = m_Snapshot.Find(k);
            if (pos == Entry.NotFound)
            {
                pos = m_Snapshot.AddEntry(k);
            }

            // write var into buffer, possibly adjusting entry's position and length
            using (var varBuffer = PooledNetworkBuffer.Get())
            {
                networkVariable.WriteDelta(varBuffer);
                if (varBuffer.Length > m_Snapshot.Entries[pos].Length)
                {
                    // allocate this Entry's buffer
                    m_Snapshot.AllocateEntry(ref m_Snapshot.Entries[pos], pos, (int)varBuffer.Length);
                }

//                m_Snapshot.Entries[pos].Key.TickWritten = m_NetworkManager.NetworkTickSystem.GetTick();
                // Copy the serialized NetworkVariable into our buffer
                Buffer.BlockCopy(varBuffer.GetBuffer(), 0, m_Snapshot.Buffer, m_Snapshot.Entries[pos].Position, (int)varBuffer.Length);
            }
        }

        /// <summary>
        /// Entry point when a Snapshot is received
        /// This is where we read and store the received snapshot
        /// </summary>
        /// <param name="clientId">
        /// <param name="snapshotStream">The stream to read from</param>
        public void ReadSnapshot(ulong clientId, Stream snapshotStream)
        {
            ushort snapshotTick = default;

            using (var reader = PooledNetworkReader.Get(snapshotStream))
            {
                snapshotTick = reader.ReadUInt16();

                if (!m_ClientReceivedSnapshot.ContainsKey(clientId))
                {
                    m_ClientReceivedSnapshot[clientId] = new Snapshot(m_NetworkManager, false);
                }
                var snapshot = m_ClientReceivedSnapshot[clientId];
                snapshot.ReadIndex(reader);
                snapshot.ReadBuffer(reader, snapshotStream);
            }

            SendAck(clientId, snapshotTick);
        }

        public void ReadAck(ulong clientId, Stream snapshotStream)
        {
            using (var reader = PooledNetworkReader.Get(snapshotStream))
            {
                var ackTick = reader.ReadUInt16();
                Debug.Log(string.Format("Receive ack {0} from client {1}", ackTick, clientId));
            }
        }

        public void SendAck(ulong clientId, ushort tick)
        {
            using (var buffer = PooledNetworkBuffer.Get())
            {
                using (var writer = PooledNetworkWriter.Get(buffer))
                {
                    writer.WriteUInt16(tick);
                }

                m_NetworkManager.MessageSender.Send(clientId, NetworkConstants.SNAPSHOT_ACK,
                    NetworkChannel.SnapshotExchange, buffer);
                buffer.Dispose();
            }

        }

        // todo --M1--
        // This is temporary debugging code. Once the feature is complete, we can consider removing it
        // But we could also leave it in in debug to help developers
        private void DebugDisplayStore(Snapshot block, string name)
        {
            string table = "=== Snapshot table === " + name + " ===\n";
            for (int i = 0; i < block.LastEntry; i++)
            {
                table += string.Format("NetworkVariable {0}:{1}:{2} written {5}, range [{3}, {4}] ", block.Entries[i].Key.NetworkObjectId, block.Entries[i].Key.BehaviourIndex,
                    block.Entries[i].Key.VariableIndex, block.Entries[i].Position, block.Entries[i].Position + block.Entries[i].Length, block.Entries[i].Key.TickWritten);

                for (int j = 0; j < block.Entries[i].Length && j < 4; j++)
                {
                    table += block.Buffer[block.Entries[i].Position + j].ToString("X2") + " ";
                }

                table += "\n";
            }
            Debug.Log(table);
        }
    }
}