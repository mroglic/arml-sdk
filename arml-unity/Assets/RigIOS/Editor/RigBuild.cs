using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// iPhone + projektor verzija WallGame-a: kopija scene sa RigLantern-om, ARKit i iOS podešavanja, Xcode projekat.
/// Iz komandne linije (dva puta Unity): -executeMethod RigBuild.Prepare, pa -executeMethod RigBuild.BuildIOS
/// </summary>
public static class RigBuild
{
    const string Logic = "Assets/ARML/ARMLCore/Scenes/Logic.unity";
    const string WallGame = "Assets/ARML/Examples/WallGame/Scenes/WallGame.unity";
    const string RigScene = "Assets/RigIOS/RigWallGame.unity";

    [MenuItem("Rig/Make RigWallGame scene")]
    public static void MakeScene()
    {
        if (!File.Exists(RigScene)) AssetDatabase.CopyAsset(WallGame, RigScene);
        var scene = EditorSceneManager.OpenScene(RigScene);
        if (!Object.FindFirstObjectByType<RigLantern>())
            new GameObject("RIG (iPhone + projektor)").AddComponent<RigLantern>();
        EditorSceneManager.SaveScene(scene);
    }

    /// <summary>
    /// Otvara RigWallGame scenu (za pokretanje editora: -executeMethod RigBuild.OpenScene).
    /// </summary>
    public static void OpenScene() => EditorSceneManager.OpenScene(RigScene);

    /// <summary>
    /// Prvo pokretanje: scena, ARKit i podešavanja (posle njega se editor ponovo kompajlira sa ARKit define-om).
    /// </summary>
    public static void Prepare()
    {
        MakeScene();
        ConfigureXR();
        ConfigurePlayer();
    }

    [MenuItem("Rig/Build iOS")]
    public static void BuildIOS()
    {
        MakeScene();
        ConfigureXR();
        ConfigurePlayer();

        string output = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                                     "dev/arml-rig-build/xcode");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Logic, RigScene },
            locationPathName = output,
            target = BuildTarget.iOS,
            options = BuildOptions.None,
        });
        Debug.Log($"[RigBuild] {report.summary.result}, {report.summary.totalErrors} errors → {output}");
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    /// <summary>
    /// XR Plug-in Management: ARKit za iOS, pokreće se sa aplikacijom.
    /// </summary>
    static void ConfigureXR()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget))
        {
            if (!AssetDatabase.IsValidFolder("Assets/XR")) AssetDatabase.CreateFolder("Assets", "XR");
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }
        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.iOS))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.iOS);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.iOS))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.iOS);
        settings.InitManagerOnStart = true;
        XRPackageMetadataStore.AssignLoader(settings.AssignedSettings, "UnityEngine.XR.ARKit.ARKitLoader", BuildTargetGroup.iOS);
        // ARKit paket u batch modu ubacuje svoju nativnu biblioteku samo ako je ovaj define već bio pri kompajliranju
        // editora (sam ga dodaje tek iz koda koji od njega zavisi), pa se dodaje ovde; build zato ide u dva pokretanja.
        var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS);
        if (!defines.Contains("UNITY_XR_ARKIT_LOADER_ENABLED"))
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.iOS, defines + ";UNITY_XR_ARKIT_LOADER_ENABLED");
        EditorUtility.SetDirty(perTarget);
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Slika kamere (passthrough na telefonu) u URP-u se crta samo uz ARBackgroundRendererFeature u rendereru.
    /// </summary>
    static void AddARBackgroundFeature()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
        {
            var data = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (!data || data.rendererFeatures.Exists(f => f is UnityEngine.XR.ARFoundation.ARBackgroundRendererFeature)) continue;
            var feature = ScriptableObject.CreateInstance<UnityEngine.XR.ARFoundation.ARBackgroundRendererFeature>();
            feature.name = "ARBackgroundRendererFeature";
            AssetDatabase.AddObjectToAsset(feature, data);
            data.rendererFeatures.Add(feature);
            var serialized = new SerializedObject(data);
            var map = serialized.FindProperty("m_RendererFeatureMap");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            map.InsertArrayElementAtIndex(map.arraySize);
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
            Debug.Log($"[RigBuild] ARBackgroundRendererFeature dodat u {data.name}");
        }
        AssetDatabase.SaveAssets();
    }

    static void ConfigurePlayer()
    {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.milosroglic.armlwallgame");
        PlayerSettings.productName = "ARML Wall";
        PlayerSettings.iOS.appleDeveloperTeamID = "3PX22FSZX9";
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;
        PlayerSettings.iOS.targetOSVersionString = "16.0";
        PlayerSettings.iOS.cameraUsageDescription = "Kamera prati položaj rig-a (ARKit).";
        PlayerSettings.iOS.microphoneUsageDescription = "Glasovne komande u igri.";
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
        // Rig aplikacija šalje kalibraciju linkom armlwall://kalibracija?…
        PlayerSettings.iOS.iOSUrlSchemes = new[] { "armlwall" };

        AddARBackgroundFeature();

        // mreža na zidu (RigLantern) pravi materijal iz koda, pa shader mora uvek biti u buildu
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        var graphics = new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset"));
        var included = graphics.FindProperty("m_AlwaysIncludedShaders");
        bool present = false;
        for (int i = 0; i < included.arraySize; i++) present |= included.GetArrayElementAtIndex(i).objectReferenceValue == unlit;
        if (unlit && !present)
        {
            included.InsertArrayElementAtIndex(included.arraySize);
            included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = unlit;
            graphics.ApplyModifiedProperties();
        }
    }
}
