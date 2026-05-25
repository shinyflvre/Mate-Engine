using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class DeleteAIHistory : MonoBehaviour
{
    [Header("UI Button to delete AI history")]
    public Button deleteButton;

    [Tooltip("Base filename for AI history. Default is 'ZomeAI'.")]
    public string fileName = "ZomeAI";

    void Start()
    {
        if (deleteButton != null)
        {
            deleteButton.onClick.AddListener(DeleteHistoryFiles);
        }
        else
        {
            Debug.LogWarning("[DeleteAIHistory] Delete Button is not assigned.");
        }
    }

    public void DeleteHistoryFiles()
    {
        string jsonPath = MateEnginePaths.DataFilePath(fileName + ".json");
        string cachePath = MateEnginePaths.DataFilePath(fileName + ".cache");

        bool deletedSomething = false;

        if (File.Exists(jsonPath))
        {
            File.Delete(jsonPath);
            Debug.Log("[DeleteAIHistory] Deleted: " + jsonPath);
            deletedSomething = true;
        }

        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
            Debug.Log("[DeleteAIHistory] Deleted: " + cachePath);
            deletedSomething = true;
        }

        if (!deletedSomething)
        {
            Debug.LogWarning("[DeleteAIHistory] No AI history files found at: " + jsonPath + " or " + cachePath);
        }
    }
}
