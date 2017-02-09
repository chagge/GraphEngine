// Graph Engine
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using System.Net;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Linq;

using Trinity;
using Trinity.Daemon;
using Trinity.Network;
using Trinity.Storage;
using Trinity.Utilities;
using Trinity.Diagnostics;
using System.Runtime.CompilerServices;

namespace Trinity
{
    /// <summary>
    /// Provides global runtime information about the system. It also provides methods for safely exiting the system. This class cannot be inherited.
    /// </summary>
    public static partial class Global
    {
        public static void Initialize()
        {
            lock (s_storage_init_lock)
            {
                if (s_master_init_flag) return;
                _LoadTSLExtensions();
                _ScanForTSLStorageExtension();
                s_master_init_flag = true;
            }
        }

        public static void Uninitialize()
        {
            lock (s_storage_init_lock)
                lock (s_comm_instance_lock)
                {
                    if (communication_instance != null)
                    {
                        communication_instance.Stop();
                    }

                    generic_cell_ops = null;
                    storage_schema = null;
                    if (isLocalStorageInited)
                    {
                        local_storage.Dispose();
                        local_storage = null;
                        isLocalStorageInited = false;
                    }

                    if (isCloudStorageInited)
                    {
                        cloud_storage.Dispose();
                        isCloudStorageInited = false;
                    }

                    BackgroundThread.Stop();
                    s_master_init_flag = false;
                }
        }

        /// <summary>
        /// Creates a cloud storage instance with the specified name.
        /// </summary>
        /// <param name="config">A ClusterConfig instance.</param>
        /// <returns>The newly created cloud storage instance.</returns>
        public static MemoryCloud CreateCloudStorage(ClusterConfig config)
        {
            lock (s_storage_init_lock)
            {
                MemoryCloud mc = new MemoryCloud(config);
                mc.RegisterGenericOperationsProvider(generic_cell_ops);
                s_registered_memoryclouds.Add(mc);
                return mc;
            }
        }

        /// <summary>
        /// Creates a cloud storage instance with the specified name.
        /// </summary>
        /// <param name="configFile">The file path of a configuration file.</param>
        /// <returns>The newly created cloud storage instance.</returns>
        public static MemoryCloud CreateCloudStorage(string configFile)
        {
            return CreateCloudStorage(ClusterConfig._LegacyLoadClusterConfig(configFile));
        }

        /// <summary>
        /// Loads a TSL storage extension assembly from the specified file.
        /// </summary>
        /// <param name="assemblyFilePath">The path of the TSL extension assembly.</param>
        /// <param name="ignoreNonIncrementalStorageSchemaChanges">
        /// If false, throw exception when the newly loaded extension has a non-incremental 
        /// change on storage schema, compared to the existing storage schema(if any).
        /// </param>
        public static void LoadTSLStorageExtension(string assemblyFilePath, bool ignoreNonIncrementalStorageSchemaChanges = false)
        {
            lock (s_storage_init_lock)
            {
                var old_storage_schema = storage_schema;
                var old_genops_provider = generic_cell_ops;

                var loaded_tuple = _LoadTSLStorageExtension(Assembly.LoadFrom(assemblyFilePath));
                var new_storage_schema = loaded_tuple.Item2;
                var new_genops_provider = loaded_tuple.Item1;

                if (new_storage_schema == null || new_genops_provider == null) { throw new InvalidOperationException("The specified assembly is not a TSL extension."); }

                if (old_storage_schema != null && !ignoreNonIncrementalStorageSchemaChanges)
                {
                    // check for non-incremental changes

                    var old_schema_signatures = old_storage_schema.CellTypeSignatures;
                    var new_schema_signatures = new_storage_schema.CellTypeSignatures;
                    var incremental = true;
                    var sigs_len = old_schema_signatures.Count();

                    if (new_schema_signatures.Count() < sigs_len) { incremental = false; }
                    else { incremental = Enumerable.SequenceEqual(old_schema_signatures, new_schema_signatures.Take(sigs_len)); }

                    if (!incremental)
                    {
                        storage_schema = old_storage_schema;
                        generic_cell_ops = old_genops_provider;
                        throw new InvalidOperationException("Non-incremental storage schema changes found.");
                    }
                }

                _RegisterTSLStorageExtension(loaded_tuple.Item1, loaded_tuple.Item2);

                if (local_storage != null) { local_storage.RegisterGenericOperationsProvider(generic_cell_ops); }
                foreach (var mc in s_registered_memoryclouds) { mc.RegisterGenericOperationsProvider(generic_cell_ops); }

                Log.WriteLine(LogLevel.Info, "TSL storage extension loaded from {0}", assemblyFilePath);
            }
        }

        /// <summary>
        /// Represents the local memory storage. Contains methods to access, manipulate cells in the local memory storage. Contains methods to send loopback messages.
        /// </summary>
        public static Storage.LocalMemoryStorage LocalStorage
        {
            get
            {
                if (!isLocalStorageInited)
                {
                    lock (s_storage_init_lock)
                    {
                        if (!isLocalStorageInited)
                        {
                            local_storage = new Storage.LocalMemoryStorage();
                            local_storage.RegisterGenericOperationsProvider(generic_cell_ops);

                            Thread.MemoryBarrier();
                            isLocalStorageInited = true;
                        }
                    }
                }
                return Global.local_storage;
            }
        }

        /// <summary>
        /// Represents the memory cloud storage. Contains methods to access, manipulate cells in the memory cloud. Contains methods to send messages to other Trinity servers.
        /// </summary>
        public static Storage.MemoryCloud CloudStorage
        {
            get
            {
                if (!isCloudStorageInited)
                {
                    lock (s_storage_init_lock)
                    {
                        if (!isCloudStorageInited) //Important: this must be checked again
                        {
                            if (cloud_storage == null)
                            {
                                cloud_storage = CreateCloudStorage(TrinityConfig.CurrentClusterConfig);
                                cloud_storage.Open(false);
                            }
                            Thread.MemoryBarrier();
                            isCloudStorageInited = true;
                        }
                    }
                }
                return cloud_storage;
            }
        }

        /// <summary>
        /// Gets the number of servers in current Trinity cluster.
        /// </summary>
        public static int ServerCount
        {
            get
            {
                return CloudStorage.ServerCount;
            }
        }

        /// <summary>
        /// Gets the number of proxies in current Trinity cluster.
        /// </summary>
        public static int ProxyCount
        {
            get
            {
                return CloudStorage.ProxyCount;
            }
        }

        /// <summary>
        /// Obsolete. Use MyServerId instead.
        /// </summary>
        public static int MyServerID
        {
            get
            {
                return MyServerId;
            }
        }

        /// <summary>
        /// Obsolete. Use MyProxyId instead.
        /// </summary>
        public static int MyProxyID
        {
            get
            {
                return MyProxyId;
            }
        }

        /// <summary>
        /// Gets the ID of current server instance in the cluster.
        /// </summary>
        public static int MyServerId
        {
            get
            {
                return CloudStorage.MyServerId;
            }
        }

        /// <summary>
        /// Gets the ID of current proxy instance in the cluster.
        /// </summary>
        public static int MyProxyId
        {
            get
            {
                return CloudStorage.MyProxyId;
            }
        }

        /// <summary>
        /// Represents the storage schema defined in the storage extension TSL assembly.
        /// If no cell types are defined in referenced TSL assemblies, an empty schema
        /// will be returned.
        /// </summary>
        public static IStorageSchema StorageSchema
        {
            get
            {
                return storage_schema;
            }
        }

        /// <summary>
        /// Represents the communication schema associated with the started TrinityServer or TrinityProxy.
        /// If no server/proxy are started, or a default server/proxy is started, an empty schema
        /// will be returned.
        /// </summary>
        public static ICommunicationSchema CommunicationSchema
        {
            get
            {
                var comm_instance = CommunicationInstance;
                if (comm_instance == null)
                {
                    return new DefaultCommunicationSchema();
                }

                return comm_instance.GetCommunicationSchema();
            }
        }

        /// <summary>
        /// Represents the running communication instance (a TrinityServer or a TrinityProxy).
        /// If no server/proxy are started, the value is null.
        /// </summary>
        public static CommunicationInstance CommunicationInstance
        {
            get
            {
                lock (s_comm_instance_lock)
                {
                    return communication_instance;
                }
            }
            internal set
            {
                lock (s_comm_instance_lock)
                {
                    if (communication_instance != null && value != null)
                    {
                        throw new InvalidOperationException("Cannot start multiple communication instances.");
                    }
                    communication_instance = value;
                }
            }
        }
    }
}
