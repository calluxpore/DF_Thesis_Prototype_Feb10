using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.IO;
using GLTFast;
using TMPro;
using NaughtyAttributes;
using Newtonsoft.Json;
// This script integrates the workflow triggered by a button press.
// Workflow:
// 1. Capture an image and generate a caption via GeminiAPI.
// 2. Use the caption as prompt for FalAi to generate an image.
// 3. Use StableFast3DClient logic to create a 3D model from the generated image (saved with a timestamp using persistent storage).
// 4. Load and display the 3D model using GLTFast.

public class CameraImageTo3DWorkflow : MonoBehaviour {

    // UI Button to start the workflow. Assign via Inspector.
    public Toggle startWorkflowButton;

    // Optional UI text to display the generated caption.
    public TMP_Text captionText;

    void Start() {
        Debug.Log("CameraImageTo3DWorkflow Ready.");
        if(startWorkflowButton != null) {
            startWorkflowButton.onValueChanged.AddListener((isOn) => {
                    StartCoroutine(RunWorkflowCoroutine());
                
            });
        } else {
            Debug.LogWarning("Start Workflow Button is not assigned.");
        }
    }
    [Button("Test Workflow")]
    IEnumerator RunWorkflowCoroutine() {
        // Step 1: Capture image and generate caption using GeminiAPI
        Texture2D screenTexture = GetScreenCaptureTexture();
        if(screenTexture == null) {
            Debug.LogError("No screen capture texture available.");
            yield break;
        }
        
        // Convert texture to a readable RGBA32 texture
        Texture2D readableTexture = new Texture2D(screenTexture.width, screenTexture.height, TextureFormat.RGBA32, false);
        RenderTexture rt = RenderTexture.GetTemporary(screenTexture.width, screenTexture.height, 0);
        Graphics.Blit(screenTexture, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        readableTexture.ReadPixels(new Rect(0, 0, screenTexture.width, screenTexture.height), 0, 0);
        readableTexture.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        // Generate caption from the image using GeminiTextToText
        string caption = "";
        Task<string> captionTask = GeminiAPI.GenerateCaptionFromImageAsync(readableTexture, 
            "Your task is to create a prompt for an AI image generator. The prompt will generate a 2D image of a 3D model to help the user remember the object at the center of the image. This is for the memory palace technique.");
        yield return new WaitUntil(() => captionTask.IsCompleted);
        if(captionTask.IsCompletedSuccessfully) {
            caption = captionTask.Result;
            Debug.Log("Gemini caption: " + caption);
            if(captionText != null)
                captionText.text = caption;
        } else {
            Debug.LogError("Failed to get caption from Gemini.");
            yield break;
        }

        // Step 2: Use FalAi to generate an image from the caption
        string falKey = "YOUR_FALAI_API_KEY"; // Replace with your actual FAL key
        string submitUrl = "https://queue.fal.run/fal-ai/flux/dev";
        var data = new { prompt = caption };
        var headers = new Dictionary<string, string> {
            { "Authorization", "Key " + falKey },
            { "Content-Type", "application/json" }
        };

        FalAiClient.FalAiSubmitResponse submitResponse = null;
        Task<FalAiClient.FalAiSubmitResponse> submitTask = Task.Run(async () => {
            return await PostRequest.PostJsonAsync<FalAiClient.FalAiSubmitResponse>(submitUrl, data, headers);
        });
        yield return new WaitUntil(() => submitTask.IsCompleted);
        submitResponse = submitTask.Result;
        if(submitResponse == null || string.IsNullOrEmpty(submitResponse.request_id)) {
            Debug.LogError("FalAI request submission failed.");
            yield break;
        }
        Debug.Log("FalAI Request ID: " + submitResponse.request_id);

        // Poll for FalAI result
        FalAiClient.FalAiResultResponse falAiResult = null;
        Task<FalAiClient.FalAiResultResponse> pollTask = Task.Run(async () => {
            return await PollForResult(submitResponse.request_id, headers);
        });
        yield return new WaitUntil(() => pollTask.IsCompleted);
        falAiResult = pollTask.Result;
        if(falAiResult == null || falAiResult.images == null || falAiResult.images.Count == 0) {
            Debug.LogError("FalAI did not return any images.");
            yield break;
        }
        string generatedImageUrl = falAiResult.images[0].url;
        Debug.Log("FalAI generated image URL: " + generatedImageUrl);

        // Step 3: Create 3D model from the generated image using StableFast3DClient logic
        byte[] imageBytes = null;
        Task<byte[]> downloadTask = Task.Run(async () => {
            using(HttpClient client = new HttpClient()) {
                try {
                    return await client.GetByteArrayAsync(generatedImageUrl);
                } catch(Exception ex) {
                    Debug.LogError("Error downloading generated image: " + ex.Message);
                    return null;
                }
            }
        });
        yield return new WaitUntil(() => downloadTask.IsCompleted);
        imageBytes = downloadTask.Result;
        if(imageBytes == null) {
            yield break;
        }

        // Prepare multipart/form-data content with timestamped image file name
        string imageTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string imageFileName = "image_" + imageTimestamp + ".png";
        MultipartFormDataContent multipartContent = new MultipartFormDataContent();
        ByteArrayContent imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        imageContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
            Name = "\"image\"",
            FileName = "\"" + imageFileName + "\""
        };
        multipartContent.Add(imageContent, "image", imageFileName);

        string stabilityApiKey = "YOUR_STABILITY_API_KEY"; // Replace with your actual API key
        byte[] glbBytes = null;
        Task<byte[]> stabilityTask = Task.Run(async () => {
            using(HttpClient client = new HttpClient()) {
                client.DefaultRequestHeaders.Add("authorization", "Bearer " + stabilityApiKey);
                try {
                    var response = await client.PostAsync("https://api.stability.ai/v2beta/3d/stable-fast-3d", multipartContent);
                    if(response.IsSuccessStatusCode) {
                        Debug.Log("3D model generated from StabilityAI.");
                        return await response.Content.ReadAsByteArrayAsync();
                    } else {
                        string errorResp = await response.Content.ReadAsStringAsync();
                        Debug.LogError("Error response from StabilityAI: " + errorResp);
                        return null;
                    }
                } catch(Exception ex) {
                    Debug.LogError("Error calling StabilityAI for 3D model: " + ex.Message);
                    return null;
                }
            }
        });
        yield return new WaitUntil(() => stabilityTask.IsCompleted);
        glbBytes = stabilityTask.Result;
        if(glbBytes == null) {
            yield break;
        }

        // Save the GLB file using persistent data storage with a timestamp in the name
        string modelsFolder = Path.Combine(Application.persistentDataPath, "3DModels");
        if(!Directory.Exists(modelsFolder)) {
            Directory.CreateDirectory(modelsFolder);
        }
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string glbFileName = "3d_model_" + timestamp + ".glb";
        string glbFilePath = Path.Combine(modelsFolder, glbFileName);
        try {
            File.WriteAllBytes(glbFilePath, glbBytes);
            Debug.Log("Saved GLB file to: " + glbFilePath);
        } catch(Exception ex) {
            Debug.LogError("Error saving GLB file: " + ex.Message);
            yield break;
        }

        // Step 4: Load and display the 3D model using GLTFast
        GameObject modelObject = new GameObject("LoadedModel");
        if(Camera.main != null) {
            modelObject.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 0.5f;
        }
        var gltfAsset = modelObject.AddComponent<GLTFast.GltfAsset>();
        Task<bool> loadTask = gltfAsset.Load(glbFilePath);
        yield return new WaitUntil(() => loadTask.IsCompleted);
        if(loadTask.Result) {
            Debug.Log("3D model loaded successfully from: " + glbFilePath);
        } else {
            Debug.LogError("Failed to load 3D model from: " + glbFilePath);
        }

        yield break;
    }

    // Helper to get the screen capture texture from DisplayCaptureManager
    Texture2D GetScreenCaptureTexture() {
        if(DisplayCaptureManager.Instance != null) {
            return DisplayCaptureManager.Instance.ScreenCaptureTexture;
        }
        return null;
    }

    // Helper method to poll for FalAI result
    async Task<FalAiClient.FalAiResultResponse> PollForResult(string requestId, Dictionary<string, string> headers) {
        string statusUrl = "https://queue.fal.run/fal-ai/flux/requests/" + requestId + "/status";
        FalAiClient.FalAiResultResponse result = null;
        int attempts = 0;
        int maxAttempts = 30; // Maximum polling attempts (approx. 60 seconds total with 2 sec delay each)

        using(HttpClient client = new HttpClient()) {
            while (attempts < maxAttempts) {
                attempts++;
                // Set headers (excluding Content-Type) and force no-cache
                foreach(var header in headers) {
                    if(!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) {
                        if(client.DefaultRequestHeaders.Contains(header.Key))
                            client.DefaultRequestHeaders.Remove(header.Key);
                        client.DefaultRequestHeaders.Add(header.Key, header.Value);
                    }
                }
                // Force no-cache for this GET request
                if(client.DefaultRequestHeaders.Contains("Cache-Control"))
                    client.DefaultRequestHeaders.Remove("Cache-Control");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

                HttpResponseMessage statusResponse = null;
                try {
                    statusResponse = await client.GetAsync(statusUrl);
                    statusResponse.EnsureSuccessStatusCode();
                } catch(Exception ex) {
                    Debug.LogError("Error querying FalAI status: " + ex.Message);
                    break;
                }
                string resJson = await statusResponse.Content.ReadAsStringAsync();
                var serializerSettings = new Newtonsoft.Json.JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
                FalAiClient.StatusResponse statusObj = JsonConvert.DeserializeObject<FalAiClient.StatusResponse>(resJson, serializerSettings);
                if(statusObj == null) {
                    Debug.LogError("FalAI status deserialization returned null. Raw JSON: " + resJson);
                    break;
                }
                Debug.Log("FalAI status: " + statusObj.status);
                if(!string.IsNullOrEmpty(statusObj.status) && statusObj.status.Equals("completed", StringComparison.OrdinalIgnoreCase)) {
                    break;
                }
                await Task.Delay(2000);
            }

            if (attempts >= maxAttempts) {
                Debug.LogError("FalAI polling timed out after " + attempts + " attempts.");
                return null;
            }

            // Force no-cache on final GET
            if(client.DefaultRequestHeaders.Contains("Cache-Control"))
                client.DefaultRequestHeaders.Remove("Cache-Control");
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

            string resultUrl = "https://queue.fal.run/fal-ai/flux/requests/" + requestId;
            HttpResponseMessage resultResponse = await client.GetAsync(resultUrl);
            resultResponse.EnsureSuccessStatusCode();
            string resultJson = await resultResponse.Content.ReadAsStringAsync();
            var serializerSettingsResult = new Newtonsoft.Json.JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
            result = JsonConvert.DeserializeObject<FalAiClient.FalAiResultResponse>(resultJson, serializerSettingsResult);
        }
        return result;
    }
}

// Stub for DisplayCaptureManager. Replace with actual implementation in your project.
public class DisplayCaptureManager : MonoBehaviour {
    private static DisplayCaptureManager _instance;
    public static DisplayCaptureManager Instance {
        get {
            if(_instance == null) {
                GameObject go = new GameObject("DisplayCaptureManager");
                _instance = go.AddComponent<DisplayCaptureManager>();
            }
            return _instance;
        }
    }

    public Texture2D ScreenCaptureTexture;

    void Awake() {
        if(ScreenCaptureTexture == null) {
            ScreenCaptureTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            Color fillColor = Color.gray;
            Color[] fillPixels = new Color[256 * 256];
            for (int i = 0; i < fillPixels.Length; i++) {
                fillPixels[i] = fillColor;
            }
            ScreenCaptureTexture.SetPixels(fillPixels);
            ScreenCaptureTexture.Apply();
        }
    }
}

