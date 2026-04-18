using UnityEngine;
using UnityEngine.Events;
using Goodgulf.Building;
using Goodgulf.Logging;
using Goodgulf.Pathfinding;

namespace Goodgulf.GameLogic
{

    public class GameManager : MonoBehaviour, IDebuggable
    {
        public Camera SceneCamera;
        public Camera PlayerCamera;
        public GameObject Player;
        
        [Header("Events")] 
        public UnityEvent OnTerrainReady;

        [Header("Debug")]
        [SerializeField, Tooltip("Enable verbose logging for this component")]
        private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;


        public static GameManager Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            DisablePlayerObject();

            // Show everything during development
            GameLogger.GlobalMinLevel = LogLevel.Verbose;

        }

        public void DisablePlayerObject()
        {
            if (SceneCamera)
            {
                SceneCamera.enabled = true;
            }
            else GameLogger.Error("Scene Camera is not assigned");

            if (PlayerCamera)
            {
                PlayerCamera.enabled = false;
            }
            else GameLogger.Error("Player Camera is not assigned");

            if (Player)
            {
                Player.SetActive(false);
            }
            else GameLogger.Error("Player is not assigned");
        }

        
        public void EnablePlayerObject()
        {
            if(SceneCamera)
                SceneCamera.enabled = false;
            if(PlayerCamera)
                PlayerCamera.enabled = true;
            if(Player)
                Player.SetActive(true);
            
        }

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            
        }

        // Update is called once per frame
        void Update()
        {

        }

        public void InitializeGameLogic()
        {
            GameLogger.Info("Initializing Game Logic");

            EnablePlayerObject();

            GameLogger.Info("Builder SetPlayer");

            Builder.Instance.CacheTerrains();
            
            Builder.Instance.SetPlayer(Player);

            GameLogger.Info("Invoke OnTerrainReady");
            
            OnTerrainReady?.Invoke();

            GameLogger.Info("Invoke Done");
            
            GameLogger.Warning("Initializing TerrainGraphIntegration");
            TerrainGraphIntegration.Instance.InitializeListeners();
            TerrainGraphIntegration.Instance.Initialize();
        }
        
        
    }


}