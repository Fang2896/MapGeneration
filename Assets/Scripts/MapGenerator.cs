using System;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using System.Threading;

public class MapGenerator : MonoBehaviour
{
    public enum DrawMode
    {
        NoiseMap,
        ColorMap,
        Mesh,
        FalloffMap
    };
    public DrawMode drawMode;

    public TerrainData terrainData;
    public NoiseData noiseData;

    [Range(0, 6)]
    public int editorPreviewLOD;
    
    public bool autoUpdate;
    
    public TerrainType[] regions;
    static MapGenerator instance;

    private float[,] falloffMap;

    private Queue<MapThreadInfo<MapData>> mapDataThreadInfoQueue = new Queue<MapThreadInfo<MapData>>();
    private Queue<MapThreadInfo<MeshData>> meshDataThreadInfoQueue = new Queue<MapThreadInfo<MeshData>>();

    private void Awake() {
        falloffMap = FalloffGenerator.GenerateFalloffMap(mapChunkSize);
    }

    // 当没在play的时候，希望能实时调整map
    void OnValueUpdated() {
        if (!Application.isPlaying) {
            DrawMapInEditor();
        }
    }

    // unity不能超过65000vertex
    // 原来是241，现在要border compensate，故改为239
    // 进一步迭代：如果用FlatShading的话，仍然要更改成95
    public static int mapChunkSize {
        get {
            if (instance == null) {
                instance = FindObjectOfType<MapGenerator>();
            }

            if (instance.terrainData.useFlatShading)
                return 95;
            else {
                return 239;
            }
        }
    }

    public void DrawMapInEditor() {
        MapData mapData = GenerateMapData(Vector2.zero);
        
        MapDisplay display = FindObjectOfType<MapDisplay>();
        if (drawMode == DrawMode.NoiseMap)
            display.DrawTexture(TextureGenerator.TextureFromHeightMap(mapData.heightMap));
        else if (drawMode == DrawMode.ColorMap)
            display.DrawTexture(TextureGenerator.TextureFromColorMap(mapData.colorMap, mapChunkSize, mapChunkSize));
        else if (drawMode == DrawMode.Mesh)
            display.DrawMesh(MeshGenerator.GenerateTerrainMesh(mapData.heightMap, terrainData.meshHeightMultiplier, terrainData.meshHeightCurve, editorPreviewLOD, terrainData.useFlatShading),
                TextureGenerator.TextureFromColorMap(mapData.colorMap, mapChunkSize, mapChunkSize));
        else if (drawMode == DrawMode.FalloffMap)
            display.DrawTexture(TextureGenerator.TextureFromHeightMap(FalloffGenerator.GenerateFalloffMap(mapChunkSize)));
    }

    /******* 多线程实现 ********/
    public void RequestMapData(Vector2 center, Action<MapData> callback) {
        ThreadStart threadStart = delegate {
            MapDataThread(center ,callback);
        };

        new Thread(threadStart).Start();
    }

    // 生产者，生产MapData
    void MapDataThread(Vector2 center, Action<MapData> callback) {
        MapData mapData = GenerateMapData(center);
        lock (mapDataThreadInfoQueue) {
            mapDataThreadInfoQueue.Enqueue(new MapThreadInfo<MapData>(callback, mapData));
        }

    }

    public void RequestMeshData(MapData mapData, int lod, Action<MeshData> callback) {
           ThreadStart threadStart = delegate {
               MeshDataThread(mapData, lod, callback);
           };
           
           new Thread(threadStart).Start();
    }
    
    // 生产者，生产MeshData
    void MeshDataThread(MapData mapData ,int lod, Action<MeshData> callback) {
        MeshData meshData =
            MeshGenerator.GenerateTerrainMesh(mapData.heightMap, terrainData.meshHeightMultiplier, terrainData.meshHeightCurve, lod, terrainData.useFlatShading);
        lock (meshDataThreadInfoQueue) {
            meshDataThreadInfoQueue.Enqueue(new MapThreadInfo<MeshData>(callback, meshData));
        }
    }

    private void Update() {
        // 消费者，执行buffer内的回调函数，清空Queue
        if (mapDataThreadInfoQueue.Count > 0) {
            for (int i = 0; i < mapDataThreadInfoQueue.Count; i++) {
                MapThreadInfo<MapData> threadInfo = mapDataThreadInfoQueue.Dequeue();
                threadInfo.callback(threadInfo.parameter);
            }
        }

        if (meshDataThreadInfoQueue.Count > 0) {
            for (int i = 0; i < meshDataThreadInfoQueue.Count; i++) {
                MapThreadInfo<MeshData> threadInfo = meshDataThreadInfoQueue.Dequeue();
                threadInfo.callback(threadInfo.parameter);  // 用相应的参数，调用Queue中的回调函数
            }
        }
    }

    MapData GenerateMapData(Vector2 center) {
        // 生成大2格的。防止边缘向量错误计算， compensate the border
        float[,] noiseMap = Noise.GenerateNoiseMap(
            mapChunkSize + 2, mapChunkSize + 2, noiseData.seed, noiseData.noiseScale, noiseData.octaves, noiseData.persistance, noiseData.lacunarity, center + noiseData.offset, noiseData.normalizeMode);
        Color[] colorMap = new Color[mapChunkSize * mapChunkSize];

        for (int y = 0; y < mapChunkSize; y++) {
            for (int x = 0; x < mapChunkSize; x++) {
                if (terrainData.useFalloff) {
                    noiseMap[x, y] = Mathf.Clamp01(noiseMap[x, y] - falloffMap[x, y]);
                }
                
                float currentHeight = noiseMap[x, y];
                // 根据noise value来决定是哪种TerrainType
                for (int i = 0; i < regions.Length; i++) {
                    if (currentHeight >= regions[i].height) {
                        colorMap[y * mapChunkSize + x] = regions[i].color;
                    }
                    else {
                        break;  // 确保不会颜色错误
                    }
                }
            }
        }
        
        // visualize noise map and color map
        return new MapData(noiseMap, colorMap);
    }
    
    // be sure the range of parameters
    void OnValidate() {
        // OnValueUpdated使用了 System.Action 委托作为事件的类型。
        // 事件是一种特殊的委托，它允许其他代码（通常是外部代码）订阅并在事件触发时执行相应的操作。
        // 值改变的时候就触发
        // 但就这样订阅的话，每次改变值都会调用OnValueUpdated数百次
        // 故先取消订阅，再订阅，这样能避免订阅很多次（small trick）
        if (terrainData != null) {
            terrainData.OnValueUpdated -= OnValueUpdated;
            terrainData.OnValueUpdated += OnValueUpdated;   // 订阅OnvalueUpdate
        }
        if (noiseData != null) {
            noiseData.OnValueUpdated -= OnValueUpdated;
            noiseData.OnValueUpdated += OnValueUpdated;
        }
        
        falloffMap = FalloffGenerator.GenerateFalloffMap(mapChunkSize);
    }

    // 回调函数struct
    struct MapThreadInfo<T>
    {
        public readonly Action<T> callback;
        public readonly T parameter;

        public MapThreadInfo(Action<T> callback, T parameter) {
            this.callback = callback;
            this.parameter = parameter;
        }
    }
}


[System.Serializable]
public struct TerrainType
{
    public string name;
    public float height;
    public Color color;
}

public struct MapData
{
    public readonly float[,] heightMap;
    public readonly Color[] colorMap;

    public MapData(float[,] heightMap, Color[] colorMap) {
        this.heightMap = heightMap;
        this.colorMap = colorMap;
    }
}