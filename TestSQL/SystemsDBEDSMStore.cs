﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using SQLLiteExtensions;
using System.Data.Common;
using System.Data;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace EliteDangerousCore.DB
{
    public partial class SystemsDB
    {
        #region Table Update from JSON FILE

        public static long ParseEDSMJSONFile(string filename, bool[] grididallow, ref DateTime date, Func<bool> cancelRequested, Action<string> reportProgress, string tableposfix = "", bool presumeempty = false, string debugoutputfile = null)
        {
            using (StreamReader sr = new StreamReader(filename))         // read directly from file..
                return ParseEDSMJSON(sr, grididallow, ref date, cancelRequested, reportProgress, tableposfix, presumeempty, debugoutputfile);
        }

        public static long ParseEDSMJSONString(string data, bool[] grididallow, ref DateTime date, Func<bool> cancelRequested, Action<string> reportProgress, string tableposfix = "", bool presumeempty = false, string debugoutputfile = null)
        {
            using (StringReader sr = new StringReader(data))         // read directly from file..
                return ParseEDSMJSON(sr, grididallow, ref date, cancelRequested, reportProgress, tableposfix, presumeempty, debugoutputfile);
        }

        public static long ParseEDSMJSON(TextReader sr, bool[] grididallow, ref DateTime date, Func<bool> cancelRequested, Action<string> reportProgress, string tablepostfix = "", bool presumeempty = false, string debugoutputfile = null)
        {
            using (JsonTextReader jr = new JsonTextReader(sr))
                return ParseEDSMJSON(jr, grididallow, ref date, cancelRequested, reportProgress, tablepostfix, presumeempty, debugoutputfile);
        }

        // set tempostfix to use another set of tables


        public static long ParseEDSMJSON(JsonTextReader jr, 
                                        bool[] grididallowed,       // null = all, else grid bool value
                                        ref DateTime maxdate,       // updated with latest date
                                        Func<bool> cancelRequested,
                                        Action<string> reportProgress,
                                        string tablepostfix,        // set to add on text to table names to redirect to another table
                                        bool tablesareempty = false,     // set to presume table is empty, so we don't have to do look up queries
                                        string debugoutputfile = null
                                        )
        {
            sectoridcache = new Dictionary<long, Sector>();     
            sectornamecache = new Dictionary<string, Sector>(); 

            SQLiteConnectionSystem cn = new SQLiteConnectionSystem(mode: SQLLiteExtensions.SQLExtConnection.AccessMode.Writer);

            GetStartingIds(cn, tablepostfix, tablesareempty, out long nextnameid, out long nextsectorid);

            StreamWriter sw = debugoutputfile != null ? new StreamWriter(debugoutputfile) : null;

            long updates = 0;
            const int BlockSize = 100000;
            int Limit = int.MaxValue;
            bool jr_eof = false;

            DbCommand selectSectorCmd = cn.CreateSelect("Sectors" + tablepostfix, "id", "name = @sname AND gridid = @gid", new string[] { "sname", "gid" }, new DbType[] { DbType.String, DbType.Int32 });

            reportProgress?.Invoke("Being EDSM Download");

            while (!cancelRequested() && jr_eof == false)
            {
                int recordstostore = 0;

                while (true)
                {
                    try
                    {
                        if (jr.Read())                                                      // collect a decent amount
                        {
                            if (jr.TokenType == JsonToken.StartObject)
                            {
                                EDSMFileEntry d = new EDSMFileEntry();

                                if (d.Deserialize(jr) && d.id >= 0 && d.name.HasChars() && d.z != int.MinValue)     // if we have a valid record
                                {
                                    int gridid = GridId.Id(d.x, d.z);
                                    if (grididallowed == null || ( grididallowed.Length > gridid && grididallowed[gridid]))    // allows a null or small grid
                                    {
                                        CreateNewUpdate(selectSectorCmd, d, gridid, tablesareempty, ref maxdate, ref nextsectorid);
                                        recordstostore++;
                                    }
                                }

                                if (--Limit == 0)
                                {
                                    jr_eof = true;
                                    break;
                                }

                                if (recordstostore >= BlockSize)
                                    break;
                            }
                        }
                        else
                        {
                            jr_eof = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("EDSM JSON file exception " + ex.ToString());
                        jr_eof = true;                                                                              // stop read, but let it continue to finish this section
                    }
                }

                System.Diagnostics.Debug.WriteLine("Process " + BaseUtils.AppTicks.TickCountLap("L1") + "   " + updates);

                if (recordstostore > 0)
                {
                    updates += StoreNewEntries(cn,  ref nextnameid, tablepostfix, sw);

                    reportProgress?.Invoke("EDSM Download updated " + updates);
                }

                if (jr_eof)
                    break;

                if (SQLiteConnectionSystem.IsReadWaiting)
                {
                    System.Threading.Thread.Sleep(20);      // just sleepy for a bit to let others use the db
                }
            }

            System.Diagnostics.Debug.WriteLine("Process " + BaseUtils.AppTicks.TickCountLap("L1") + "   " + updates);
            reportProgress?.Invoke( "EDSM Download finished, updated " + updates);

            if (sw != null)
                sw.Close();

            selectSectorCmd.Dispose();
            cn.Dispose();

            sectoridcache = null;
            sectornamecache = null;

            return updates;
        }

        #endregion

        #region UPGRADE FROM 102

        // take old system table and turn to new.  tablesarempty=false is normal, only set to true if using this code for checking replace algorithm

        public static long UpgradeDB102to200(Func<bool> cancelRequested, Action<string> reportProgress, string tablepostfix, bool tablesareempty = false, int maxgridid = int.MaxValue)
        {
            sectoridcache = new Dictionary<long, Sector>();
            sectornamecache = new Dictionary<string, Sector>();

            SQLiteConnectionSystem cn = new SQLiteConnectionSystem(mode: SQLLiteExtensions.SQLExtConnection.AccessMode.ReaderWriter);

            long nextnameid = 1;                // tables are empty..
            long nextsectorid = 1;

            long updates = 0;
            long Limit =  long.MaxValue;

            DateTime maxdate = DateTime.MinValue;       // we don't pass this back due to using the same date
            reportProgress?.Invoke("Being System DB upgrade");

            DbCommand selectSectorCmd = cn.CreateSelect("Sectors" + tablepostfix, "id", "name = @sname AND gridid = @gid", new string[] { "sname", "gid" }, new DbType[] { DbType.String, DbType.Int32 });

            List<int> gridids = DB.GridId.AllId();
            BaseUtils.AppTicks.TickCountLap("UTotal");

            //int debug_z = 0;

            foreach (int gridid in gridids)  // using grid id to keep chunks a good size.. can't read and write so can't just read the whole.
            {
                if (cancelRequested())
                {
                    updates = -1;
                    break;
                }

                if (gridid == maxgridid)        // for debugging
                    break;
                    
                DbCommand selectPrev = cn.CreateSelect("EdsmSystems s", "s.EdsmId,s.x,s.y,s.z,n.Name,s.UpdateTimeStamp", "s.GridId = " + gridid.ToStringInvariant(),
                                                        joinlist: new string[] { "LEFT OUTER JOIN SystemNames n ON n.EdsmId=s.EdsmId" });

                int recordstostore = 0;

                using (DbDataReader reader = selectPrev.ExecuteReader())       // find name:gid
                {
                    BaseUtils.AppTicks.TickCountLap("U1");

                    while (reader.Read())
                    {
                        try
                        {
                            EDSMFileEntry d = new EDSMFileEntry();
                            d.id = (long)reader[0];
                            d.x = (int)(long)reader[1];
                            d.y = (int)(long)reader[2];
                            d.z = (int)(long)reader[3];
                            d.name = (string)reader[4];
                            d.date = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromSeconds((long)reader["UpdateTimestamp"]);
                            int grididentry = GridId.Id(d.x, d.z);  // because i don't trust the previous gridid - it should be the same as the outer loop, but lets recalc

                            //if (!tablesareempty)  d.z = debug_z++;  // for debug checking

                            CreateNewUpdate(selectSectorCmd, d, grididentry, tablesareempty, ref maxdate, ref nextsectorid);      // not using gridid on purpose to double check it.
                            recordstostore++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("Reading prev table" + ex);
                        }
                    }
                }

                selectPrev.Dispose();

                //System.Diagnostics.Debug.WriteLine("Reader took " + BaseUtils.AppTicks.TickCountLap("U1") + " in " + gridid + "  " + recordpos + " total " + recordstostore);

                if (recordstostore >= 0)
                {
                    updates += StoreNewEntries(cn, ref nextnameid, tablepostfix, null);
                    reportProgress?.Invoke("System DB upgrade processed " + updates);

                    Limit -= recordstostore;

                    if (Limit <= 0)
                        break;

                    if (SQLiteConnectionSystem.IsReadWaiting)
                    {
                        System.Threading.Thread.Sleep(20);      // just sleepy for a bit to let others use the db
                    }
                }
                var tres1 = BaseUtils.AppTicks.TickCountLapDelta("U1");
                var tres2 = BaseUtils.AppTicks.TickCountFrom("UTotal");
                System.Diagnostics.Debug.WriteLine("Sector " + gridid + " took " + tres1.Item1 + " store " + recordstostore + " total " + updates + " " + ((float)tres1.Item2 / (float)recordstostore) + " cumulative " + tres2);
            }

            reportProgress?.Invoke("System DB complete, processed " + updates);

            selectSectorCmd.Dispose();
            cn.Dispose();

            sectoridcache = null;
            sectornamecache = null;

            return updates;
        }

        #endregion


        #region Table Update Helpers

        // create a new entry for insert in the sector tables 
        private static void CreateNewUpdate(DbCommand selectSectorCmd , EDSMFileEntry d, int gid, bool tablesareempty, ref DateTime maxdate, ref long nextsectorid)
        {
            TableWriteData data = new TableWriteData() { edsm = d, classifier = new EliteNameClassifier(d.name), gridid = gid };

            if (d.date > maxdate)                                   // for all, record last recorded date processed
                maxdate = d.date;

            Sector t = null, prev = null;

            if (!sectornamecache.ContainsKey(data.classifier.SectorName))   // if unknown to cache
            {
                sectornamecache[data.classifier.SectorName] = t = new Sector(data.classifier.SectorName, gridid: data.gridid);   // make a sector of sectorname and with gridID n , id == -1
            }
            else
            {
                t = sectornamecache[data.classifier.SectorName];        // find the first sector of name
                while (t != null && t.GId != data.gridid)        // if GID of sector disagrees
                {
                    prev = t;                          // go thru list
                    t = t.NextSector;
                }

                if (t == null)      // still not got it, its a new one.
                {
                    prev.NextSector = t = new Sector(data.classifier.SectorName, gridid: data.gridid);   // make a sector of sectorname and with gridID n , id == -1
                }
            }

            if (t.Id == -1)   // if unknown sector ID..
            {
                if (tablesareempty)     // if tables are empty, we can just presume its id
                {
                    t.Id = nextsectorid++;      // insert the sector with the guessed ID
                    t.insertsec = true;
                    sectoridcache[t.Id] = t;    // and cache
                    //System.Diagnostics.Debug.WriteLine("Made sector " + t.Name + ":" + t.GId);
                }
                else
                {
                    selectSectorCmd.Parameters[0].Value = t.Name;   
                    selectSectorCmd.Parameters[1].Value = t.GId;

                    using (DbDataReader reader = selectSectorCmd.ExecuteReader())       // find name:gid
                    {
                        if (reader.Read())      // if found name:gid
                        {
                            t.Id = (long)reader[0];
                        }
                        else
                        {
                            t.Id = nextsectorid++;      // insert the sector with the guessed ID
                            t.insertsec = true;
                        }

                        sectoridcache[t.Id] = t;                // and cache
                      //  System.Diagnostics.Debug.WriteLine("Made sector " + t.Name + ":" + t.GId);
                    }
                }
            }

            if (t.edsmdatalist == null)
                t.edsmdatalist = new List<TableWriteData>(5000);

            t.edsmdatalist.Add(data);                       // add to list of systems to process for this sector
        }

        private static long StoreNewEntries(SQLiteConnectionSystem cn,
                                           ref long nextnameid,
                                           string tablepostfix = "",        // set to add on text to table names to redirect to another table
                                           StreamWriter sw = null
                                        )
        {
            long updates = 0;

            ////////////////////////////////////////////////////////////// push all new data to the db without any selects

            SQLExtTransactionLock<SQLiteConnectionSystem> tl = new SQLExtTransactionLock<SQLiteConnectionSystem>();     // not using on purpose.
            tl.OpenWriter();
            DbTransaction txn = cn.BeginTransaction();

            DbCommand insertSectorCmd = cn.CreateInsert("Sectors" + tablepostfix, new string[] { "name", "gridid" }, new DbType[] { DbType.String, DbType.Int32 }, txn);

            DbCommand insertorreplaceSysCmd = cn.CreateInsert("Systems" + tablepostfix, new string[] { "sectorid", "nameid", "x", "y", "z", "edsmid" }, 
                                new DbType[] { DbType.Int64, DbType.Int64, DbType.Int32, DbType.Int32, DbType.Int32, DbType.Int64 }, txn , insertorreplace:true);

            DbCommand insertNameCmd = cn.CreateInsert("Names" + tablepostfix, new string[] { "name" }, new DbType[] { DbType.String }, txn);

            foreach (var kvp in sectoridcache)                  // all sectors cached, id is unique so its got all sectors                           
            {
                Sector t = kvp.Value;

                if (t.insertsec)         // if we have been told to insert the sector, do it
                {
                    insertSectorCmd.Parameters[0].Value = t.Name;     // make a new one so we can get the ID
                    insertSectorCmd.Parameters[1].Value = t.GId;
                    insertSectorCmd.ExecuteNonQuery();
                    //System.Diagnostics.Debug.WriteLine("Written sector " + t.id + " " +t.Name + " now clean");
                    t.insertsec = false;
                }

                if (t.edsmdatalist != null)       // if updated..
                {
                    foreach (var data in t.edsmdatalist)            // now write the star list in this sector
                    {
                        try
                        {
                            if (!data.classifier.IsStandard)    // if non standard, we assign a new ID
                            {
                                data.classifier.NameId = nextnameid++;
                                insertNameCmd.Parameters[0].Value = data.classifier.StarName;       // insert a new name
                                insertNameCmd.ExecuteNonQuery();
                            }

                            insertorreplaceSysCmd.Parameters[0].Value = t.Id;
                            insertorreplaceSysCmd.Parameters[1].Value = data.classifier.ID;
                            insertorreplaceSysCmd.Parameters[2].Value = data.edsm.x;
                            insertorreplaceSysCmd.Parameters[3].Value = data.edsm.y;
                            insertorreplaceSysCmd.Parameters[4].Value = data.edsm.z;
                            insertorreplaceSysCmd.Parameters[5].Value = data.edsm.id;       // in the event a new entry has the same edsmid, the system table edsmid is replace with new data
                            insertorreplaceSysCmd.ExecuteNonQuery();

                            if (sw != null)
                                sw.WriteLine(data.classifier.ToString() + " " + data.edsm.x + "," + data.edsm.y + "," + data.edsm.z + ", EDSM:" + data.edsm.id + " Grid:" + data.gridid);

                            updates++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("general exception during insert - ignoring " + ex.ToString());
                        }

                    }
                }

                t.edsmdatalist = null;     // and delete back
            }

            txn.Commit();

            insertSectorCmd.Dispose();
            insertorreplaceSysCmd.Dispose();
            insertNameCmd.Dispose();
            txn.Dispose();
            tl.Dispose();

            return updates;

        }

        static private void GetStartingIds(SQLiteConnectionSystem cn, string tablepostfix, bool tablesareempty, out long nextnameid, out long nextsectorid)
        {
            nextnameid = nextsectorid = 1;

            if ( !tablesareempty)
            {
                nextnameid = cn.MaxIdOf("Names" + tablepostfix, "id");
                nextsectorid = cn.MaxIdOf("Sectors" + tablepostfix, "id");
            }
        }

        #endregion

        #region Upgrade from 102

        #endregion

        #region Internal Vars and Classes

        static Dictionary<long, Sector> sectoridcache;          // only used during store operation
        static Dictionary<string, Sector> sectornamecache;

        private class Sector
        {
            public long Id;
            public int GId;
            public string Name;

            public Sector NextSector;       // memory only field, link to next in list

            public Sector(string name, long id = -1, int gridid = -1 )
            {
                this.Name = name;
                this.GId = gridid;
                this.Id = id;
                this.NextSector = null;
            }

            // for write table purposes only

            public List<TableWriteData> edsmdatalist;
            public bool insertsec = false;
        };

        private class TableWriteData
        {
            public EDSMFileEntry edsm;
            public EliteNameClassifier classifier;
            public int gridid;
        }

        public class EDSMFileEntry
        {
            public bool Deserialize(JsonReader rdr)
            {
                while (rdr.Read() && rdr.TokenType == JsonToken.PropertyName)
                {
                    string field = rdr.Value as string;
                    switch (field)
                    {
                        case "name":
                            name = rdr.ReadAsString();
                            break;
                        case "id":
                            id = rdr.ReadAsInt32() ?? 0;
                            break;
                        case "date":
                            date = rdr.ReadAsDateTime() ?? DateTime.MinValue;
                            break;
                        case "coords":
                            {
                                if (rdr.TokenType != JsonToken.StartObject)
                                    rdr.Read();

                                while (rdr.Read() && rdr.TokenType == JsonToken.PropertyName)
                                {
                                    field = rdr.Value as string;
                                    double? v = rdr.ReadAsDouble();
                                    if (v == null)
                                        return false;
                                    int vi = (int)(v * 128.0);

                                    switch (field)
                                    {
                                        case "x":
                                            x = vi;
                                            break;
                                        case "y":
                                            y = vi;
                                            break;
                                        case "z":
                                            z = vi;
                                            break;
                                    }
                                }

                                break;
                            }
                        default:
                            rdr.Read();
                            JToken.Load(rdr);
                            break;
                    }
                }

                return true;
            }

            public string name;
            public long id = -1;
            public DateTime date;
            public int x = int.MinValue;
            public int y = int.MinValue;
            public int z = int.MinValue;
        }

        #endregion
    }
}


