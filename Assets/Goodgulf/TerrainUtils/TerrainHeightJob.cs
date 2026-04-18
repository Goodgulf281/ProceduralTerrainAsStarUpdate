using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Goodgulf.TerrainUtils
{
    // Structure to hold parameters for different biomes
    public struct BiomeParams
    {
        public float baseHeight;  // Base height offset of the biome
        public float amplitude;   // Height variation amplitude
        public float noiseScale;  // Scale of noise used for height calculation
        public int octaves;       // Number of noise layers
        public float lacunarity;  // Frequency scaling factor between successive noise layers
        public float gain;        // Amplitude reduction factor per octave
    }

    //[BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    [BurstCompile(FloatPrecision.Standard, FloatMode.Strict)]
    public struct TerrainHeightJob : IJobParallelFor
    {
        public int resolution;          // Resolution of terrain heightmap
        public float2 worldOrigin;      // Origin point in world coordinates
        public float2 terrainSize;      // Size of the terrain

        public float biomeScale;        // Scale for biome distribution
        public float mountainRidgeScale; // Scale for mountain ridges
        public float mountainRidgeStrength; // Strength of mountain ridge features

        public float erosionScale;      // Scale for erosion effect
        public float erosionStrength;   // Intensity of erosion
        public float erosionDepth;      // Depth alteration due to erosion

        public float riverScale;        // Scale for river paths
        public float riverWidth;        // Width of rivers
        public float riverDepth;        // Depth of river carving

        public int biomeCount;          // Number of different biomes

        [ReadOnly] public NativeArray<BiomeParams> biomes;  // Array of biome parameters

        [WriteOnly] public NativeArray<float> heights;      // Output array storing computed terrain heights
        
        public void Execute(int index)
        {
            // Compute grid position from linear index
            int x = index % resolution;
            int y = index / resolution;

            // Normalize grid coordinates
            float nx = (float)x / (resolution - 1);
            float ny = (float)y / (resolution - 1);

            // Map normalized coordinates to world space
            float worldX = worldOrigin.x + nx * terrainSize.x;
            float worldZ = worldOrigin.y + ny * terrainSize.y;

            // Sample height at this world location
            float height = SampleHeight(worldX, worldZ);

            // Clamp height to [0,1] and store in the heights array
            heights[index] = math.clamp(height, 0f, 1f);
        }

        // ================= HEIGHT PIPELINE =================

        float SampleHeight(float worldX, float worldZ)
        {
            // Sample height based on biome configuration
            float height = SampleBiomeHeight(worldX, worldZ);

            // Determine mountain ridge influence
            float mountainStrength = MountainStrength(worldX, worldZ);
            if (mountainStrength > 0f)
            {
                // Add ridge features to the height value
                float ridge = RidgedNoise(
                    worldX * mountainRidgeScale,
                    worldZ * mountainRidgeScale
                );

                height += ridge * mountainRidgeStrength * mountainStrength;
            }

            // Apply erosion and river effects
            height = ApplyErosion(height, worldX, worldZ);
            height = ApplyRiver(height, worldX, worldZ);

            return height;
        }

        // ================= BIOMES =================

        float SampleBiomeHeight(float worldX, float worldZ)
        {
            // Generate a noise value to determine biome blending
            float biomeValue = noise.snoise(new float2(
                worldX * biomeScale,
                worldZ * biomeScale
            ));

            // Normalize the noise value
            biomeValue = biomeValue * 0.5f + 0.5f;

            // Calculate the exact position within available biomes
            float biomePos = biomeValue * (biomes.Length - 1);
            int biomeIndex = (int)math.floor(biomePos);

            // Select two nearest biomes for interpolation
            int biomeA = math.clamp(biomeIndex, 0, biomes.Length - 1);
            int biomeB = math.clamp(biomeIndex + 1, 0, biomes.Length - 1);

            // Blend between selected biomes based on fractional biome position
            float blend = biomePos - biomeIndex;

            // Get heights for blended biomes
            float hA = SampleSingleBiome(biomes[biomeA], worldX, worldZ);
            float hB = SampleSingleBiome(biomes[biomeB], worldX, worldZ);

            return math.lerp(hA, hB, blend);  // Linearly interpolate heights
        }

        float SampleSingleBiome(BiomeParams biome, float worldX, float worldZ)
        {
            // Compute Fractal Brownian Motion based noise for the biome
            float n = FBM(
                worldX * biome.noiseScale,
                worldZ * biome.noiseScale,
                biome.octaves,
                biome.lacunarity,
                biome.gain
            );

            // Normalize noise value
            n = n * 0.5f + 0.5f;

            // Return final height after applying base height and amplitude
            return biome.baseHeight + n * biome.amplitude;
        }

        float MountainStrength(float worldX, float worldZ)
        {
            // Generate a noise value indicating potential mountain presence
            float biomeValue = noise.snoise(new float2(
                worldX * biomeScale,
                worldZ * biomeScale
            ));

            // Normalize the noise value
            biomeValue = biomeValue * 0.5f + 0.5f;
            float biomePos = biomeValue * (biomeCount - 1);

            // Define mountain start threshold
            float mountainStart = biomeCount - 2;

            // Determine strength of mountains based on biome position
            return math.saturate((biomePos - mountainStart));
        }

        // ================= NOISE =================

        float FBM(float x, float y, int octaves, float lacunarity, float gain)
        {
            float value = 0f;   // Accumulated noise value
            float amp = 1f;     // Initial amplitude
            float freq = 1f;    // Initial frequency

            for (int i = 0; i < octaves; i++)
            {
                // Add noise at current frequency and amplitude
                value += noise.snoise(new float2(x * freq, y * freq)) * amp;

                // Update frequency and amplitude for next octave
                freq *= lacunarity;
                amp *= gain;
            }

            return value;  // Return accumulated noise value
        }

        float RidgedNoise(float x, float y)
        {
            float value = 0f;   // Accumulated noise value
            float amp = 1f;     // Initial amplitude
            float freq = 1f;    // Initial frequency

            for (int i = 0; i < 4; i++)
            {
                // Compute ridged noise by inversing absolute noise values
                float n = noise.snoise(new float2(x * freq, y * freq));
                n = 1f - math.abs(n);
                value += n * amp;

                // Update frequency and amplitude for next iteration
                freq *= 2.2f;
                amp *= 0.5f;
            }

            return value;  // Return accumulated ridged noise value
        }

        // ================= EROSION =================

        float ApplyErosion(float height, float worldX, float worldZ)
        {
            // Compute slope based on noise gradients in x and z directions
            float dx =
                noise.snoise(new float2((worldX + 1f) * erosionScale, worldZ * erosionScale)) -
                noise.snoise(new float2((worldX - 1f) * erosionScale, worldZ * erosionScale));

            float dz =
                noise.snoise(new float2(worldX * erosionScale, (worldZ + 1f) * erosionScale)) -
                noise.snoise(new float2(worldX * erosionScale, (worldZ - 1f) * erosionScale));

            float slope = math.abs(dx) + math.abs(dz);
            float erosion = math.saturate(slope * erosionStrength);

            // Adjust height based on calculated erosion
            return height - erosion * erosionDepth;
        }

        // ================= RIVERS =================

        float ApplyRiver(float height, float worldX, float worldZ)
        {
            // Compute river flow intensity using noise
            float flow = math.abs(
                noise.snoise(new float2(worldX * riverScale, worldZ * riverScale))
            );

            // Smoothly adjust terrain height to create river bed effect
            float river = math.smoothstep(riverWidth, 0f, flow);
            
            // Use height to mask river depth
            float heightMask = math.saturate(1f - height * 4f);

            return height - river * heightMask * riverDepth;
        }
    }
}
