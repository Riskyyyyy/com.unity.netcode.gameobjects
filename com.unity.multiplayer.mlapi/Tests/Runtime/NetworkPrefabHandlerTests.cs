using System;
using System.Collections.Generic;
using UnityEngine;
using MLAPI.Spawning;
using NUnit.Framework;


namespace MLAPI.RuntimeTests
{
    /// <summary>
    /// The NetworkPrefabHandler unit tests validates:
    /// Registering with GameObject, NetworkObject, or GlobalObjectIdHash
    /// Newly assigned rotation or position values for newly spawned NetworkObject instances are valid
    /// Destroying a newly spawned NetworkObject instance works
    /// Removing a INetworkPrefabInstanceHandler is removed and can be verified (very last check)
    /// </summary>
    public class NetworkPrefabHandlerTests
    {
        [Test]
        public void NetworkPrefabHandlerClass()
        {
            //Only used to create a network object based game asset
            Assert.IsTrue(NetworkManagerHelper.StartNetworkManager());
            var testPrefabObjectName = "NetworkPrefabHandlerTestObject";

            Guid baseObjectID = NetworkManagerHelper.AddGameNetworkObject(testPrefabObjectName);
            NetworkObject baseObject = NetworkManagerHelper.InstantiatedNetworkObjects[baseObjectID];

            var networkPrefabHandler = new NetworkPrefabHandler();
            var networkPrefaInstanceHandler = new NetworkPrefaInstanceHandler(baseObject);

            var prefabPosition = new Vector3(1.0f, 5.0f, 3.0f);
            var prefabRotation = new Quaternion(1.0f, 0.5f, 0.4f, 0.1f);

            //Register via GameObject
            var gameObjectRegistered = networkPrefabHandler.AddHandler(baseObject.gameObject, networkPrefaInstanceHandler);

            //Test result of registering via GameObject reference
            Assert.True(gameObjectRegistered);

            var spawnedObject = networkPrefabHandler.HandleNetworkPrefabSpawn(baseObject.GlobalObjectIdHash, 0, prefabPosition, prefabRotation);

            //Test that something was instantiated
            Assert.NotNull(spawnedObject);

            //Test that this is indeed an instance of our original object
            Assert.True(spawnedObject.name.Contains(testPrefabObjectName));

            //Test for position and rotation
            Assert.True(prefabPosition == spawnedObject.transform.position);
            Assert.True(prefabRotation == spawnedObject.transform.rotation);

            networkPrefabHandler.HandleNetworkPrefabDestroy(spawnedObject);     //Destroy our prefab instance
            networkPrefabHandler.RemoveHandler(baseObject);                     //Remove our handler

            //Register via NetworkObject
            gameObjectRegistered = networkPrefabHandler.AddHandler(baseObject, networkPrefaInstanceHandler);

            //Test result of registering via NetworkObject reference
            Assert.True(gameObjectRegistered);

            //Change it up
            prefabPosition = new Vector3(2.0f, 1.0f, 5.0f);
            prefabRotation = new Quaternion(4.0f, 1.5f, 5.4f, 5.1f);

            spawnedObject = networkPrefabHandler.HandleNetworkPrefabSpawn(baseObject.GlobalObjectIdHash, 0, prefabPosition, prefabRotation);

            //Test that something was instantiated
            Assert.NotNull(spawnedObject);

            //Test that this is indeed an instance of our original object
            Assert.True(spawnedObject.name.Contains(testPrefabObjectName));

            //Test for position and rotation
            Assert.True(prefabPosition == spawnedObject.transform.position);
            Assert.True(prefabRotation == spawnedObject.transform.rotation);

            networkPrefabHandler.HandleNetworkPrefabDestroy(spawnedObject);     //Destroy our prefab instance
            networkPrefabHandler.RemoveHandler(baseObject);                     //Remove our handler

            //Register via GlobalObjectIdHash
            gameObjectRegistered = networkPrefabHandler.AddHandler(baseObject.GlobalObjectIdHash, networkPrefaInstanceHandler);

            //Test result of registering via GlobalObjectIdHash reference
            Assert.True(gameObjectRegistered);

            //Change it up
            prefabPosition = new Vector3(6.0f, 4.0f, 1.0f);
            prefabRotation = new Quaternion(3f, 2f, 4f, 1f);

            spawnedObject = networkPrefabHandler.HandleNetworkPrefabSpawn(baseObject.GlobalObjectIdHash, 0, prefabPosition, prefabRotation);

            //Test that something was instantiated
            Assert.NotNull(spawnedObject);

            //Test that this is indeed an instance of our original object
            Assert.True(spawnedObject.name.Contains(testPrefabObjectName));

            //Test for position and rotation
            Assert.True(prefabPosition == spawnedObject.transform.position);
            Assert.True(prefabRotation == spawnedObject.transform.rotation);

            networkPrefabHandler.HandleNetworkPrefabDestroy(spawnedObject);     //Destroy our prefab instance
            networkPrefabHandler.RemoveHandler(baseObject);                     //Remove our handler

            Assert.False(networkPrefaInstanceHandler.StillHasInstances());
        }

        [SetUp]
        public void Setup()
        {
            //Create, instantiate, and host
            NetworkManagerHelper.StartNetworkManager();
        }

        [TearDown]
        public void TearDown()
        {
            //Stop, shutdown, and destroy
            NetworkManagerHelper.ShutdownNetworkManager();
        }
    }

    /// <summary>
    /// The Prefab instance handler to use for this test
    /// </summary>
    public class NetworkPrefaInstanceHandler : INetworkPrefabInstanceHandler
    {
        private NetworkObject m_NetworkObject;

        private List<NetworkObject> m_Instances;

        public NetworkObject HandleNetworkPrefabSpawn(ulong ownerClientId, Vector3 position, Quaternion rotation)
        {
            var networkObjectInstance = UnityEngine.Object.Instantiate(m_NetworkObject.gameObject).GetComponent<NetworkObject>();
            networkObjectInstance.transform.position = position;
            networkObjectInstance.transform.rotation = rotation;
            m_Instances.Add(networkObjectInstance);
            return networkObjectInstance;
        }

        public void HandleNetworkPrefabDestroy(NetworkObject networkObject)
        {
            var instancesContainsNetworkObject = m_Instances.Contains(networkObject);
            Assert.True(instancesContainsNetworkObject);
            m_Instances.Remove(networkObject);
            UnityEngine.Object.Destroy(networkObject.gameObject);
        }

        public bool StillHasInstances()
        {
            return (m_Instances.Count > 0);
        }

        public NetworkPrefaInstanceHandler(NetworkObject networkObject)
        {
            m_NetworkObject = networkObject;
            m_Instances = new List<NetworkObject>();
        }
    }
}