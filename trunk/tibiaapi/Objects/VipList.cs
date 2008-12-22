using System;
using System.Collections.Generic;
using Tibia.Packets;

namespace Tibia.Objects
{
    public class VipList
    {
        private Client client;

        public VipList(Client c)
        {
            client = c;
        }

        public List<Vip> GetPlayers()
        {
            List<Vip> players = new List<Vip>();
            for (uint i = Addresses.Vip.Start; i < Addresses.Vip.End; i += Addresses.Vip.Step_Players)
            {
                players.Add(new Vip(client,i));
            }
            return players;
        }
        /// <summary>
        /// Get with specific id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public Vip GetPlayer(int id)
        {
            return GetPlayers().Find(delegate(Vip v)
                {
                    return v.Id == id;
                });
        }
        /// <summary>
        /// Get with specific name
        /// </summary>
        /// <param name="playerName"></param>
        /// <returns></returns>
        public Vip GetPlayer(string playerName)
        {
            return GetPlayers().Find(delegate(Vip v)
            {
                return v.Name.Equals(playerName, StringComparison.CurrentCultureIgnoreCase);
            });
        }
        /// <summary>
        /// Gets a list of online players in viplist
        /// </summary>
        /// <returns></returns>
        public List<Vip> GetOnline()
        {
            List<Vip> players = new List<Vip>();
            for (uint i = Addresses.Vip.Start; i < Addresses.Vip.End; i += Addresses.Vip.Step_Players)
            {
                Vip vip = new Vip(client, i);
                if (vip.Status == Constants.VipStatus.Online)
                {
                    players.Add(vip);
                }
            }
            return players;
        }

        /// <summary>
        /// Gets list of player with specific icon
        /// </summary>
        /// <param name="icon"></param>
        /// <returns></returns>
        public List<Vip> GetPlayers(Constants.VipIcon icon)
        {
            List<Vip> players = new List<Vip>();
            for (uint i = Addresses.Vip.Start; i < Addresses.Vip.End; i += Addresses.Vip.Step_Players)
            {
                Vip vip = new Vip(client, i);
                if (vip.Icon == icon)
                {
                    players.Add(vip);
                }
            }
            return players;
        }
        /// <summary>
        /// Adds Player to VIP
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool AddPlayer(string name)
        {
            return Packets.Outgoing.VipAddPacket.Send(client, name);
        }

        /// <summary>
        /// Removes Player from VIP
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool RemovePlayer(Vip vip)
        {
            return Packets.Outgoing.VipRemovePacket.Send(client, (uint)vip.Id);
        }
    }
}
