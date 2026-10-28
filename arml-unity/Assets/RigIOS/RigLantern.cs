using System;
using System.Collections.Generic;
using System.Globalization;
using ARML.Interaction;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Lanterna na iPhone + projektor rig-u (iPhone 14 Pro + Philips PicoPix preko Lightning → HDMI).
/// Umesto SpectacularAI VIO, pozu lanterne daje ARKit: kamera lanterne = poza telefona × kalibracija projektora
/// (ista kao u Rig aplikaciji), sa objektivom projektora (vidno polje + pomak slike). Slika lanterne ide na
/// projektor (drugi ekran); telefon pokazuje sliku kamere i dugmad. Dodir ekrana je ARML "dugme" (miš 0).
/// </summary>
[DefaultExecutionOrder(-1000)] // dugme za interakciju se čita pre ARML skripti
public class RigLantern : MonoBehaviour
{
    [Header("Objektiv projektora (auto kalibracija iz Rig aplikacije)")]
    [Tooltip("Horizontalno vidno polje, stepeni")] public float fov = 29.64738f;
    [Tooltip("Pomak slike, u delovima pola širine / visine")] public float shiftX = -0.0435195f, shiftY = 0.5021024f;
    public float fyRatio = 0.999744f;
    public bool mirror;

    [Header("Projektor u odnosu na kameru telefona (ARKit oblik: x desno, y gore, z unazad)")]
    [Tooltip("Stepeni")] public float yaw = -2.949786f, pitch = 7.260281f, roll = 4.657957f;
    [Tooltip("Centimetri")] public float x = 8.216487f, y = -0.524607f, z = -9.568962f;

    // ARKit (desna ruka, kamera gleda u −z) ↔ Unity (leva ruka, kamera gleda u +z)
    static readonly Matrix4x4 FlipZ = Matrix4x4.Scale(new Vector3(1, 1, -1));

    Camera phone;
    Text status;
    Matrix4x4 reset = Matrix4x4.identity;
    Matrix4x4 lastProjector = Matrix4x4.identity;
    bool needsReset = true;
    bool tracking;
    /// <summary>Telefon pokazuje AR pogled: slika kamere i scena iz kamere telefona (inače pogled lanterne).</summary>
    bool showCamera = true;
    /// <summary>Telefon pokazuje sliku projektora (iz projektora, njegov ugao i pomak), za proveru bez projektora.</summary>
    bool showProjectorView;
    ARCameraBackground cameraBackground;
    float nextLog;
    Text logText;
    readonly Queue<string> logLines = new();
    readonly List<InputDevice> devices = new();
    string deviceList = "";
    ARPlaneManager planes;
    /// <summary>Prednja strana virtuelnog zida u sceni: sredina i normala (ka lanterni), izmereno na početku.</summary>
    Vector3? wallCenter;
    Vector3 wallNormal;
    float projectorAspect = 854f / 480;
    GameObject grid;
    /// <summary>Pozadina lanterne: njihova (bež), crna (vidi se pravi zid) ili svetlo lanterne (prigušena bež,
    /// koju ARML vinjeta pretvara u meku svetlu elipsu).</summary>
    enum Background { Theirs, Black, LanternLight }
    Background background;
    [Tooltip("Jačina svetla lanterne (deo njihove bež pozadine)")] [Range(0, 1)] public float lanternLight = 0.3f;
    Text wallLabel;
    /// <summary>ARML "Lantern Canvas": vinjeta (crna maska sa elipsom), nišan i ostali UI lanterne.</summary>
    readonly List<Canvas> lanternCanvases = new();
    /// <summary>Pozadina kamere lanterne iz ARML scene (svetlo bež).</summary>
    Color? lanternBackground;
    RectTransform interactRect;
    bool interactHeld, interactDown, interactUp;
    /// <summary>ARKit projekcija kamere telefona (iz poslednjeg kadra), za AR pogled na telefonu.</summary>
    Matrix4x4? phoneProjection;
    Matrix4x4 lastDevice = Matrix4x4.identity;
    Transform uiRoot;
    RectTransform aimMarker;

    void Awake()
    {
        Application.targetFrameRate = 60;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Screen.orientation = ScreenOrientation.LandscapeLeft;

        // ARInputManager pokreće ARKit ulazni podsistem: bez njega nema poze telefona (ni u Input System-u)
        new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

        // telefon: samo slika kamere (sadržaj ide na projektor); bez projektora preko nje crta lanterna
        var phoneObject = new GameObject("Phone Camera");
        phone = phoneObject.AddComponent<Camera>();
        phone.cullingMask = 0;
        phone.clearFlags = CameraClearFlags.SolidColor;
        phone.backgroundColor = Color.black;
        phone.depth = -100;
        phone.targetDisplay = 0;
        phoneObject.AddComponent<ARCameraManager>().frameReceived += frame =>
        {
            if (frame.projectionMatrix.HasValue) phoneProjection = frame.projectionMatrix.Value;
        };
        cameraBackground = phoneObject.AddComponent<ARCameraBackground>();

        // XR Origin samo za nalaženje pravog zida (ARKit uspravne ravni, uz LiDAR); svet sesije = svet origina
        var originObject = new GameObject("XR Origin");
        // neaktivan dok se ne postavi kamera, inače XROrigin u Awake traži Main Camera (lanternu) i upozorava
        originObject.SetActive(false);
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(originObject.transform, false);
        phoneObject.transform.SetParent(offset.transform, false);
        var origin = originObject.AddComponent<XROrigin>();
        origin.Camera = phone;
        origin.CameraFloorOffsetObject = offset;
        origin.CameraYOffset = 0;
        planes = originObject.AddComponent<ARPlaneManager>();
        planes.requestedDetectionMode = PlaneDetectionMode.Vertical;
        originObject.SetActive(true);

        BuildUI();
        Application.logMessageReceived += OnLog;
        LoadCalibration();
        Application.deepLinkActivated += OnDeepLink;
        if (!string.IsNullOrEmpty(Application.absoluteURL)) OnDeepLink(Application.absoluteURL);
        InteractionButton.Down = () => interactDown;
        InteractionButton.Held = () => interactHeld;
        InteractionButton.Up = () => interactUp;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        Application.deepLinkActivated -= OnDeepLink;
        InteractionButton.Down = InteractionButton.Held = InteractionButton.Up = null;
    }

    /// <summary>
    /// ARML "dugme" je samo dugme Interakcija na ekranu (dodir ili miš unutar njega); ostala dugmad ne interaguju.
    /// </summary>
    void Update()
    {
        bool held = false;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var touch = Input.GetTouch(i);
            if (touch.phase != TouchPhase.Ended && touch.phase != TouchPhase.Canceled
                && RectTransformUtility.RectangleContainsScreenPoint(interactRect, touch.position)) held = true;
        }
        if (Input.touchCount == 0 && Input.GetMouseButton(0)
            && RectTransformUtility.RectangleContainsScreenPoint(interactRect, Input.mousePosition)) held = true;
        interactDown = held && !interactHeld;
        interactUp = !held && interactHeld;
        interactHeld = held;
    }

    /// <summary>
    /// Kalibracija iz Rig aplikacije: dugme "Pošalji u ARML Wall" otvara armlwall://kalibracija?fov=…&shiftX=…
    /// (ista imena kao Calibration u Rig-u). Pamti se, pa važi i pri sledećem pokretanju.
    /// </summary>
    void OnDeepLink(string url)
    {
        var uri = new Uri(url);
        if (uri.Host != "kalibracija") return;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            if (parts.Length != 2 || !float.TryParse(Uri.UnescapeDataString(parts[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                continue;
            switch (parts[0])
            {
                case "fov": fov = value; break;
                case "shiftX": shiftX = value; break;
                case "shiftY": shiftY = value; break;
                case "fyRatio": fyRatio = value; break;
                case "yaw": yaw = value; break;
                case "pitch": pitch = value; break;
                case "roll": roll = value; break;
                case "x": x = value; break;
                case "y": y = value; break;
                case "z": z = value; break;
                case "mirror": mirror = value != 0; break;
            }
        }
        PlayerPrefs.SetString("rigCalibration", JsonUtility.ToJson(new StoredCalibration(this)));
        PlayerPrefs.Save();
        // nova kalibracija menja i to gde projektor gađa zid
        needsReset = true;
        Debug.Log($"[Rig] Kalibracija iz Rig-a: FOV {fov:F2}°, pomak {shiftX:F3}/{shiftY:F3}, yaw/pitch/roll {yaw:F2}/{pitch:F2}/{roll:F2}°, x/y/z {x:F1}/{y:F1}/{z:F1} cm");
    }

    void LoadCalibration()
    {
        var json = PlayerPrefs.GetString("rigCalibration", "");
        if (json == "") return;
        JsonUtility.FromJson<StoredCalibration>(json).ApplyTo(this);
        Debug.Log($"[Rig] Učitana sačuvana kalibracija (FOV {fov:F2}°).");
    }

    [Serializable]
    struct StoredCalibration
    {
        public float fov, shiftX, shiftY, fyRatio, yaw, pitch, roll, x, y, z;
        public bool mirror;

        public StoredCalibration(RigLantern r)
        {
            fov = r.fov; shiftX = r.shiftX; shiftY = r.shiftY; fyRatio = r.fyRatio; mirror = r.mirror;
            yaw = r.yaw; pitch = r.pitch; roll = r.roll; x = r.x; y = r.y; z = r.z;
        }

        public void ApplyTo(RigLantern r)
        {
            r.fov = fov; r.shiftX = shiftX; r.shiftY = shiftY; r.fyRatio = fyRatio; r.mirror = mirror;
            r.yaw = yaw; r.pitch = pitch; r.roll = roll; r.x = x; r.y = y; r.z = z;
        }
    }

    /// <summary>
    /// Poslednjih nekoliko Unity poruka na ekranu telefona (dugme "Log").
    /// </summary>
    void OnLog(string message, string stackTrace, LogType type)
    {
        string color = type switch
        {
            LogType.Error or LogType.Exception or LogType.Assert => "#ff6060",
            LogType.Warning => "#ffd040",
            _ => "#ffffff",
        };
        string line = message.Length > 160 ? message[..160] + "…" : message;
        logLines.Enqueue($"<color={color}>{line.Replace("<", "‹")}</color>");
        while (logLines.Count > 16) logLines.Dequeue();
        if (logText) logText.text = string.Join("\n", logLines);
    }

    /// <summary>
    /// Poza kamere telefona iz ARKit ulaznog uređaja (Unity koordinate, kamera gleda u +z).
    /// </summary>
    bool TryCameraPose(out Vector3 position, out Quaternion rotation)
    {
        // AR Foundation 6 daje pozu telefona kroz novi Input System (Project Settings → Active Input Handling: Both)
        var handheld = UnityEngine.InputSystem.InputSystem.GetDevice<HandheldARInputDevice>();
        if (handheld != null)
        {
            position = handheld.devicePosition.ReadValue();
            rotation = handheld.deviceRotation.ReadValue();
            if (rotation != default) return true;
        }
        InputDevices.GetDevices(devices);
        string list = "";
        foreach (var device in devices) list += $"{device.name} [{device.characteristics}]; ";
        if (list != deviceList)
        {
            deviceList = list;
            Debug.Log($"[Rig] XR uređaji: {(list == "" ? "nema" : list)}");
        }
        foreach (var device in devices)
        {
            if (device.TryGetFeatureValue(CommonUsages.colorCameraPosition, out position)
                && device.TryGetFeatureValue(CommonUsages.colorCameraRotation, out rotation)) return true;
            if (device.TryGetFeatureValue(CommonUsages.centerEyePosition, out position)
                && device.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rotation)) return true;
            if (device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation)) return true;
        }
        position = default;
        rotation = Quaternion.identity;
        return false;
    }

    void Start()
    {
        // VIO / BNO više ne pomeraju lanternu
        foreach (var provider in FindObjectsByType<SpectacularAI.DepthAI.PoseProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            provider.enabled = false;
        foreach (var mover in FindObjectsByType<CameraParentController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            mover.allowMove = false;
        InteractionTypeController.Instance?.ChangeInteractionType(true);
        HideNetworkHud();
    }

    void LateUpdate()
    {
        var lantern = Camera.main;
        if (!lantern) return;

        bool external = Display.displays.Length > 1;
        if (external && !Display.displays[1].active) Display.displays[1].Activate();
        // telefon: AR pogled (slika kamere + scena iz kamere telefona) ili pogled lanterne (dugme "Kamera")
        lantern.targetDisplay = external ? 1 : 0;
        UpdateLanternCanvases(external);
        // pogled telefona: iz kamere telefona (sa slikom kamere ili bez nje), ili slika projektora
        bool phoneAR = !showProjectorView && phoneProjection.HasValue;
        phone.cullingMask = phoneAR ? lantern.cullingMask : 0;
        phone.depth = phoneAR || external ? lantern.depth + 1 : lantern.depth - 1;
        cameraBackground.enabled = showCamera;
        phone.backgroundColor = BackgroundColor(lanternBackground ?? Color.black);
        lanternBackground ??= lantern.backgroundColor;
        lantern.clearFlags = CameraClearFlags.SolidColor;
        lantern.backgroundColor = BackgroundColor(lanternBackground.Value);

        bool hasPose = TryCameraPose(out var position, out var rotation);
        tracking = hasPose && ARSession.state == ARSessionState.SessionTracking;
        if (tracking)
        {
            lastDevice = Matrix4x4.TRS(position, rotation, Vector3.one);
            lastProjector = Matrix4x4.TRS(position, rotation, Vector3.one) * Extrinsic();
            if (needsReset) ResetPose();
        }

        // lanterna u sceni: ORIGIN (CameraParentController) × reset × projektor, okrenuta ka sredini slike
        // projektora (ARML nišani pravcem forward, a sredina slike nije na osi objektiva zbog pomaka slike)
        var parent = FindFirstObjectByType<CameraParentController>();
        var world = (parent ? parent.transform.localToWorldMatrix : Matrix4x4.identity) * reset * lastProjector;
        var toCenter = Quaternion.FromToRotation(Vector3.forward, ImageCenter(projectorAspect));
        lantern.transform.SetPositionAndRotation(world.GetPosition(), world.rotation * toCenter);
        if (phoneAR)
        {
            var phoneWorld = (parent ? parent.transform.localToWorldMatrix : Matrix4x4.identity) * reset * lastDevice;
            phone.transform.SetPositionAndRotation(phoneWorld.GetPosition(), phoneWorld.rotation);
            phone.projectionMatrix = phoneProjection.Value;
        }

        float aspect = external
            ? (float)Display.displays[1].renderingWidth / Display.displays[1].renderingHeight
            : (float)Screen.width / Screen.height;
        if (external) projectorAspect = aspect;
        // isto okretanje vraćeno u projekciji, pa slika ostaje ista: P' = P · S · R · S (S: Unity ↔ OpenGL z)
        lantern.projectionMatrix = Projection(aspect, lantern.nearClipPlane, lantern.farClipPlane)
            * FlipZ * Matrix4x4.Rotate(toCenter) * FlipZ;
        UpdateAimMarker(lantern, phoneAR);

        string trackingText = tracking ? "praćenje: dobro"
            : $"praćenje: {ARSession.state} ({ARSession.notTrackingReason}){(hasPose ? "" : ", nema poze")}";
        bool cameraAllowed = Application.HasUserAuthorization(UserAuthorization.WebCam);
        status.text = $"{trackingText} · kamera: {(cameraAllowed ? "dozvoljena" : "NIJE dozvoljena")} · projektor: {(external ? "povezan" : "nije povezan")}";
        if (Time.time > nextLog)
        {
            nextLog = Time.time + 2;
            Debug.Log($"[Rig] {status.text}");
        }
    }

    /// <summary>
    /// Trenutna poza projektora postaje ORIGIN scene: položaj i pravac (yaw); nagib ostaje stvaran.
    /// </summary>
    public void ResetPose()
    {
        if (SnapToWall()) return;
        Vector3 forward = lastProjector.rotation * Vector3.forward;
        float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        reset = Matrix4x4.TRS(lastProjector.GetPosition(), Quaternion.Euler(0, heading, 0), Vector3.one).inverse;
        needsReset = false;
    }

    /// <summary>
    /// Lepi scenu za pravi zid: zrak iz sredine slike projektora seče uspravnu ravan koju je ARKit našao (tačka +
    /// normala), a scena se
    /// postavlja tako da prednja strana virtuelnog zida (adapted_brick_wall) leži u toj ravni, sa sredinom gde rig
    /// nišani. Okreće se samo oko vertikale (oba zida su uspravna). Vraća false ako zida nema u sceni ili ispred riga.
    /// </summary>
    bool SnapToWall()
    {
        if (!FindVirtualWall()) return false;

        // sredina slike projektora nije na osi objektiva (pomak slike), pa zrak ide kroz nju
        var ray = new Ray(lastProjector.GetPosition(), lastProjector.rotation * ImageCenter(projectorAspect));
        if (!HitWall(ray, out var hitPosition, out var normal))
        {
            Debug.LogWarning($"[Rig] Pravi zid nije nađen (uspravnih ravni: {planes.trackables.count}), scena je postavljena ispred riga.");
            return false;
        }
        var horizontal = new Vector3(normal.x, 0, normal.z);

        // svet scene ← sesija: zakretanje oko y koje normalu pravog zida okreće u normalu virtuelnog, pa pomeraj
        float turn = Vector3.SignedAngle(horizontal, new Vector3(wallNormal.x, 0, wallNormal.z), Vector3.up);
        var worldFromSession = Matrix4x4.Translate(wallCenter.Value)
            * Matrix4x4.Rotate(Quaternion.Euler(0, turn, 0))
            * Matrix4x4.Translate(-hitPosition);
        var parent = FindFirstObjectByType<CameraParentController>();
        reset = (parent ? parent.transform.worldToLocalMatrix : Matrix4x4.identity) * worldFromSession;
        needsReset = false;
        Debug.Log($"[Rig] Zalepljeno za zid na {Vector3.Distance(ray.origin, hitPosition):F2} m.");
        return true;
    }

    /// <summary>
    /// UI lanterne (vinjeta, nišan) pripada slici projektora: sa projektorom ide na njegov ekran, a bez njega se
    /// na telefonu vidi samo u pogledu "Projektor" (u AR pogledu bi zaklanjao sliku kamere).
    /// </summary>
    void UpdateLanternCanvases(bool external)
    {
        if (lanternCanvases.Count == 0)
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (canvas.isRootCanvas && canvas.name.StartsWith("Lantern Canvas")) lanternCanvases.Add(canvas);
        foreach (var canvas in lanternCanvases)
        {
            if (!canvas) continue;
            canvas.targetDisplay = external ? 1 : 0;
            canvas.enabled = external || showProjectorView;
        }
    }

    /// <summary>
    /// Pravac sredine slike projektora u koordinatama projektora (Unity: z napred); zbog pomaka slike nije osa objektiva.
    /// </summary>
    Vector3 ImageCenter(float aspect)
    {
        float tanHalf = Mathf.Tan(fov * Mathf.Deg2Rad / 2);
        return new Vector3(shiftX * tanHalf, shiftY * tanHalf / aspect / fyRatio, 1).normalized;
    }

    /// <summary>
    /// AR pogled na telefonu: krug tamo gde gađa sredina slike projektora (to ARML koristi za nišanjenje), jer
    /// kamera telefona gleda u malo drugom pravcu od projektora.
    /// </summary>
    void UpdateAimMarker(Camera lantern, bool phoneAR)
    {
        var origin = lantern.transform.position;
        var forward = lantern.transform.forward;
        var target = Physics.Raycast(origin, forward, out var hit, 30, ~0, QueryTriggerInteraction.Ignore)
            ? hit.point : origin + forward * 2;
        var screen = phone.WorldToScreenPoint(target);
        bool visible = phoneAR && screen.z > 0;
        aimMarker.gameObject.SetActive(visible);
        if (visible && RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)uiRoot, screen, null, out var local))
            aimMarker.anchoredPosition = local;
    }

    /// <summary>
    /// Najbliža uspravna ravan koju zrak pogađa unutar njenih granica (uz 30 cm rezerve); normala ka rigu.
    /// </summary>
    bool HitWall(Ray ray, out Vector3 position, out Vector3 normal)
    {
        position = default;
        normal = default;
        float best = float.MaxValue;
        foreach (var plane in planes.trackables)
        {
            if (plane.alignment != PlaneAlignment.Vertical) continue;
            Vector3 n = plane.normal;
            float facing = Vector3.Dot(ray.direction, n);
            if (Mathf.Abs(facing) < 0.2f) continue;
            float t = Vector3.Dot(plane.center - ray.origin, n) / facing;
            if (t < 0.2f || t > 15 || t >= best) continue;
            Vector3 p = ray.GetPoint(t);
            // u prostoru ravni: y je normala, granice su oko centerInPlaneSpace (x, z)
            Vector3 local = plane.transform.InverseTransformPoint(p);
            Vector2 fromCenter = new Vector2(local.x, local.z) - plane.centerInPlaneSpace;
            if (Mathf.Abs(fromCenter.x) > plane.extents.x + 0.3f || Mathf.Abs(fromCenter.y) > plane.extents.y + 0.3f) continue;
            best = t;
            position = p;
            normal = Vector3.Dot(ray.origin - p, n) < 0 ? -n : n;
        }
        return best < float.MaxValue;
    }

    /// <summary>
    /// Lice virtuelnog zida: "Stencil Quad" (portal kroz koji se vidi predeo) leži tačno na licu zida od cigli,
    /// pa daje sredinu i normalu (okrenutu ka početnoj tački lanterne). Meri se jednom, pre nego što cigle popadaju.
    /// </summary>
    bool FindVirtualWall()
    {
        if (wallCenter.HasValue) return true;
        var quad = GameObject.Find("Stencil Quad")?.GetComponent<Renderer>();
        if (!quad)
        {
            Debug.LogWarning("[Rig] Nema Stencil Quad u sceni.");
            return false;
        }
        var parent = FindFirstObjectByType<CameraParentController>();
        Vector3 viewer = parent ? parent.transform.position : Vector3.zero;
        Vector3 normal = quad.transform.forward;
        normal = new Vector3(normal.x, 0, normal.z).normalized;
        if (Vector3.Dot(viewer - quad.bounds.center, normal) < 0) normal = -normal;
        wallNormal = normal;
        wallCenter = quad.bounds.center;
        Debug.Log($"[Rig] Virtuelni zid: sredina {wallCenter.Value}, veličina {quad.bounds.size}, normala {normal}");
        return true;
    }

    /// <summary>
    /// Mreža od 10 cm (svakih 50 cm deblja) na licu zida, 3 × 2 m: na projekciji se vidi da li zid stoji na pravom
    /// zidu i da li je prave veličine.
    /// </summary>
    void ToggleGrid()
    {
        if (!grid)
        {
            if (!FindVirtualWall()) return;
            grid = new GameObject("Rig mreža");
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor", new Color(0.3f, 1, 1));
            const float width = 3, height = 2;
            for (int i = 0; i <= 30; i++)
                GridBar(material, new Vector3(-width / 2 + i * 0.1f, 0, 0), new Vector3(i % 5 == 0 ? 0.008f : 0.003f, height, 0.002f));
            for (int i = 0; i <= 20; i++)
                GridBar(material, new Vector3(0, -height / 2 + i * 0.1f, 0), new Vector3(width, i % 5 == 0 ? 0.008f : 0.003f, 0.002f));
            // z mreže gleda ka lanterni, malo ispred lica zida
            grid.transform.SetPositionAndRotation(wallCenter.Value + wallNormal * 0.01f, Quaternion.LookRotation(-wallNormal, Vector3.up));
            grid.SetActive(false);
        }
        grid.SetActive(!grid.activeSelf);
    }

    void GridBar(Material material, Vector3 position, Vector3 size)
    {
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(bar.GetComponent<Collider>());
        bar.GetComponent<Renderer>().sharedMaterial = material;
        bar.transform.SetParent(grid.transform, false);
        bar.transform.localPosition = position;
        bar.transform.localScale = size;
    }

    /// <summary>
    /// "Zid": kruži kroz pozadine lanterne. Crna: projektor ne svetli van sadržaja, pa se vidi pravi zid.
    /// Svetlo lanterne: prigušena bež, ARML vinjeta je zatamnjuje ka ivicama, pa ostaje meka svetla elipsa.
    /// </summary>
    void ToggleWall()
    {
        background = (Background)(((int)background + 1) % 3);
        wallLabel.text = background switch { Background.Black => "Zid: crn", Background.LanternLight => "Zid: svetlo", _ => "Zid" };
        Debug.Log($"[Rig] Pozadina lanterne: {background}.");
    }

    Color BackgroundColor(Color theirs) => background switch
    {
        Background.Black => Color.black,
        Background.LanternLight => theirs * lanternLight,
        _ => theirs,
    };

    /// <summary>
    /// FishNet panel (Start/Stop Server, Client) se sakriva: mreža se u ovom demou ne koristi, ali ostaje upaljena
    /// jer ARML kroz nju učitava scenu igre.
    /// </summary>
    static void HideNetworkHud()
    {
        foreach (var hud in FindObjectsByType<FishNet.Example.NetworkHudCanvases>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            hud.gameObject.SetActive(false);
    }

    /// <summary>
    /// Projektor u koordinatama kamere telefona (isto kao Calibration.extrinsic u Rig aplikaciji), prebačeno u Unity.
    /// </summary>
    Matrix4x4 Extrinsic()
    {
        // ARKit: R = Ry(yaw) · Rx(pitch) · Rz(roll), desna ruka
        var arkit = RotationY(yaw) * RotationX(pitch) * RotationZ(roll);
        arkit.SetColumn(3, new Vector4(x / 100, y / 100, z / 100, 1));
        return FlipZ * arkit * FlipZ;
    }

    /// <summary>
    /// Nesimetrična piramida projektora (OpenGL oblik, kao Calibration.projection u Rig aplikaciji).
    /// </summary>
    Matrix4x4 Projection(float aspect, float near, float far)
    {
        float halfWidth = Mathf.Tan(fov * Mathf.Deg2Rad / 2) * near;
        float halfHeight = halfWidth / aspect / fyRatio;
        float l = halfWidth * (shiftX - 1), r = halfWidth * (shiftX + 1);
        float b = halfHeight * (shiftY - 1), t = halfHeight * (shiftY + 1);
        var m = Matrix4x4.zero;
        m[0, 0] = 2 * near / (r - l);
        m[1, 1] = 2 * near / (t - b);
        m[0, 2] = (r + l) / (r - l);
        m[1, 2] = (t + b) / (t - b);
        m[2, 2] = -(far + near) / (far - near);
        m[2, 3] = -2 * far * near / (far - near);
        m[3, 2] = -1;
        return mirror ? Matrix4x4.Scale(new Vector3(-1, 1, 1)) * m : m;
    }

    static Matrix4x4 RotationX(float degrees)
    {
        float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
        var m = Matrix4x4.identity;
        m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c;
        return m;
    }

    static Matrix4x4 RotationY(float degrees)
    {
        float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
        var m = Matrix4x4.identity;
        m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c;
        return m;
    }

    static Matrix4x4 RotationZ(float degrees)
    {
        float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
        var m = Matrix4x4.identity;
        m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c;
        return m;
    }

    /// <summary>
    /// Ekran telefona: stanje, "Reset" (trenutni pravac rig-a postaje ORIGIN) i veliko dugme za interakciju.
    /// Svaki dodir je za ARML pritisak dugmeta, pa dugme "Interakcija" samo pokazuje gde se tapka.
    /// </summary>
    void BuildUI()
    {
        if (!FindFirstObjectByType<EventSystem>())
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var canvasObject = new GameObject("Rig UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        uiRoot = canvas.transform;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.targetDisplay = 0;
        canvas.sortingOrder = 1000;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 740);
        scaler.matchWidthOrHeight = 1;

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        status = Label(canvas.transform, font, 26, TextAnchor.MiddleCenter);
        Place(status.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -130), new Vector2(1500, 50));

        // nišan projektora na AR pogledu telefona: prsten
        aimMarker = new GameObject("Nišan projektora", typeof(RectTransform)).GetComponent<RectTransform>();
        aimMarker.SetParent(canvas.transform, false);
        aimMarker.anchorMin = aimMarker.anchorMax = new Vector2(0.5f, 0.5f);
        aimMarker.sizeDelta = new Vector2(70, 70);
        for (int side = 0; side < 4; side++)
        {
            var tick = new GameObject("crta", typeof(Image)).GetComponent<Image>();
            tick.transform.SetParent(aimMarker, false);
            tick.color = new Color(1, 0.6f, 0.1f);
            tick.raycastTarget = false;
            bool vertical = side % 2 == 0;
            tick.rectTransform.sizeDelta = vertical ? new Vector2(5, 24) : new Vector2(24, 5);
            tick.rectTransform.anchoredPosition = side switch { 0 => new Vector2(0, 26), 1 => new Vector2(26, 0), 2 => new Vector2(0, -26), _ => new Vector2(-26, 0) };
        }
        aimMarker.gameObject.SetActive(false);

        Button(canvas.transform, font, "Reset", 30, new Color(1, 1, 1, 0.25f), ResetPose,
               new Vector2(0, 1), new Vector2(120, -60), new Vector2(180, 80));
        Button(canvas.transform, font, "Projektor", 26, new Color(1, 1, 1, 0.25f), () => showProjectorView = !showProjectorView,
               new Vector2(0, 1), new Vector2(1120, -60), new Vector2(180, 80));
        Button(canvas.transform, font, "Kamera", 30, new Color(1, 1, 1, 0.25f), () => showCamera = !showCamera,
               new Vector2(0, 1), new Vector2(320, -60), new Vector2(180, 80));
        Button(canvas.transform, font, "Mreža", 30, new Color(1, 1, 1, 0.25f), ToggleGrid,
               new Vector2(0, 1), new Vector2(720, -60), new Vector2(180, 80));
        wallLabel = Button(canvas.transform, font, "Zid", 26, new Color(1, 1, 1, 0.25f), ToggleWall,
                           new Vector2(0, 1), new Vector2(920, -60), new Vector2(180, 80)).GetComponentInChildren<Text>();
        var logPanel = new GameObject("Log", typeof(Image)).GetComponent<Image>();
        logPanel.transform.SetParent(canvas.transform, false);
        logPanel.color = new Color(0, 0, 0, 0.7f);
        logPanel.raycastTarget = false;
        Place(logPanel.rectTransform, new Vector2(0, 0), new Vector2(640, 280), new Vector2(1240, 480));
        logText = Label(logPanel.transform, font, 20, TextAnchor.LowerLeft);
        logText.supportRichText = true;
        logText.horizontalOverflow = HorizontalWrapMode.Wrap;
        logText.verticalOverflow = VerticalWrapMode.Truncate;
        logText.raycastTarget = false;
        Place(logText.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1210, 460));
        logPanel.gameObject.SetActive(false);
        Button(canvas.transform, font, "Log", 30, new Color(1, 1, 1, 0.25f), () => logPanel.gameObject.SetActive(!logPanel.gameObject.activeSelf),
               new Vector2(0, 1), new Vector2(520, -60), new Vector2(180, 80));
        interactRect = Button(canvas.transform, font, "Interakcija", 40, new Color(1, 0.6f, 0.1f, 0.85f), null,
                              new Vector2(1, 0), new Vector2(-220, 170), new Vector2(360, 260));
    }

    static Text Label(Transform parent, Font font, int size, TextAnchor anchor)
    {
        var text = new GameObject("Text", typeof(Text)).GetComponent<Text>();
        text.transform.SetParent(parent, false);
        text.font = font;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = Color.white;
        return text;
    }

    static RectTransform Button(Transform parent, Font font, string title, int size, Color color, UnityEngine.Events.UnityAction action,
                                Vector2 anchor, Vector2 position, Vector2 dimensions)
    {
        var buttonObject = new GameObject(title, typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = color;
        if (action != null) buttonObject.GetComponent<Button>().onClick.AddListener(action);
        Place(buttonObject.GetComponent<RectTransform>(), anchor, position, dimensions);
        var label = Label(buttonObject.transform, font, size, TextAnchor.MiddleCenter);
        label.text = title;
        Place(label.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, dimensions);
        return buttonObject.GetComponent<RectTransform>();
    }

    static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}
