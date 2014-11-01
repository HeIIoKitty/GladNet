﻿using Common.Register;
using GladNet.Common;
using Lidgren.Network;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GladNet.Server
{
	public class ConnectionCollection<PeerType, LidgrenType> : IEnumerable<ConnectionPair<LidgrenType, PeerType>>,
		IRegisterable<ConnectionPair<LidgrenType, PeerType>, long> where PeerType : Peer
	{
		private readonly List<PeerType> RegisteredPeers;
		private readonly List<LidgrenType> RegisteredNetConnections;

		private readonly IDictionary<long, ConnectionPair<LidgrenType, PeerType>> InternalPeerTable;

		internal ConnectionCollection()
		{
			this.RegisteredNetConnections = new List<LidgrenType>();
			this.RegisteredPeers = new List<PeerType>();
			InternalPeerTable = new Dictionary<long, ConnectionPair<LidgrenType, PeerType>>();
		}

		//Hacky workaround for not being able to implement IEnumerable<T> for both PeerType and NetConnection.
		public static implicit operator List<PeerType>(ConnectionCollection<PeerType, LidgrenType> cc)
		{
			return cc.RegisteredPeers;
		}

		public static implicit operator List<LidgrenType>(ConnectionCollection<PeerType, LidgrenType> cc)
		{
			return cc.RegisteredNetConnections;
		}

		public ConnectionPair<LidgrenType, PeerType> Get(long key)
		{
			return this.InternalPeerTable[key];
		}

		public bool HasKey(long key)
		{
			return this.InternalPeerTable.ContainsKey(key);
		}

		public bool Register(LidgrenType netConnection, PeerType peer, long key)
		{
			if (HasKey(key))
				return false;

			return this.Register(new ConnectionPair<LidgrenType, PeerType>(netConnection, peer), key);
		}

		public bool Register(ConnectionPair<LidgrenType, PeerType> obj, long key)
		{
			if (HasKey(key))
				return false;

			this.InternalPeerTable[key] = obj;

			//Also add these elements to the internal lists for easy iterating
			RegisteredPeers.Add(obj.HighlevelPeer);
			RegisteredNetConnections.Add(obj.LidgrenPeer);

			return true;
		}

		public bool UnRegister(long key)
		{
			if(!HasKey(key))
				return false;

			return this.RegisteredPeers.Remove(InternalPeerTable[key].HighlevelPeer) && this.RegisteredNetConnections.Remove(InternalPeerTable[key].LidgrenPeer)
				&& InternalPeerTable.Remove(key);
		}

		public ConnectionPair<LidgrenType, PeerType> this[long key]
		{
			get { return Get(key); }
		}

		public IEnumerator<ConnectionPair<LidgrenType, PeerType>> GetEnumerator()
		{
			return InternalPeerTable.Values.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return InternalPeerTable.GetEnumerator();
		}
	}
}
