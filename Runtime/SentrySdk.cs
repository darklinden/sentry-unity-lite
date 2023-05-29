using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Sentry;
using UnityEngine.Networking;
using UnityDebug = UnityEngine.Debug;

public class SentrySdk : MonoBehaviour
{
    private float _timeLastError = 0;
    private const float MinTime = 0.5f;
    private Breadcrumb[] _breadcrumbs;
    private int _lastBreadcrumbPos = 0;
    private int _noBreadcrumbs = 0;

    [Header("DSN of your sentry instance")]
    public string Dsn;
    [Header("Send PII like User and Computer names")]
    public bool SendDefaultPii = true;
    [Header("Enable auto generate breadcrumb")]
    public bool AutoGenerateBreadcrumb = false;
    [Header("Enable SDK debug messages")]
    public bool Debug = true;
    [Header("Override game version")]
    public string Version = "";

    private string _lastErrorMessage = "";
    private Dsn _dsn;
    private bool _initialized = false;

    private static SentrySdk _instance = null;

    public static void Init()
    {
        UnityEngine.Debug.Log("SentrySdk.Init Start");
        var sentryTxtAsset = Resources.Load<TextAsset>("Sentry");
        if (sentryTxtAsset == null)
            return;

        var sentryTxt = sentryTxtAsset.text.Trim();
        if (string.IsNullOrEmpty(sentryTxt))
            return;

        var lines = sentryTxt.Split('\n');
        if (lines.Length < 1)
            return;

        var config_Dsn = string.Empty;
        var config_Pii = false;
        var config_Breadcrumb = false;
        var config_Debug = false;

        foreach (var line in lines)
        {
            var keyVal = line.Split('=');
            if (keyVal.Length != 2)
                continue;

            var key = keyVal[0].Trim();
            var val = keyVal[1].Trim();

            switch (key)
            {
                case "Dsn":
                    config_Dsn = val;
                    break;
                case "Pii":
                    if (!bool.TryParse(val, out config_Pii))
                        config_Pii = false;
                    break;
                case "Breadcrumb":
                    if (!bool.TryParse(val, out config_Breadcrumb))
                        config_Breadcrumb = false;
                    break;
                case "Debug":
                    if (!bool.TryParse(val, out config_Debug))
                        config_Debug = false;
                    break;
            }
        }

        if (string.IsNullOrEmpty(config_Dsn))
            return;

        Dsn dsn = null;
        try
        {
            dsn = new Dsn(config_Dsn);
        }
        catch (Exception e)
        {
            UnityDebug.LogError("Cannot parse DSN: " + e.Message);
            return;
        }

        if (_instance == null)
        {
            var go = new GameObject("SentrySdk");
            _instance = go.AddComponent<SentrySdk>();
            DontDestroyOnLoad(go);
        }

        _instance.Init(dsn, config_Dsn, config_Pii, config_Breadcrumb, config_Debug);
    }

    public void Init(Dsn dsn, string dsnStr, bool pii, bool breadcrumb, bool debug)
    {
        _dsn = dsn;
        Dsn = dsnStr;
        SendDefaultPii = pii;
        AutoGenerateBreadcrumb = breadcrumb;
        Debug = debug;

        _breadcrumbs = new Breadcrumb[Breadcrumb.MaxBreadcrumbs];
        _initialized = true;

        UnityEngine.Debug.Log("SentrySdk.Init End");
    }

    public static void AddBreadcrumb(string message)
    {
        if (_instance == null)
        {
            return;
        }

        _instance.DoAddBreadcrumb(message);
    }

    public static Coroutine CaptureMessage(string message)
    {
        if (_instance == null)
        {
            return null;
        }

        return _instance.DoCaptureMessage(message);
    }

    public static Coroutine CaptureEvent(SentryEvent @event)
    {
        if (_instance == null)
        {
            return null;
        }

        return _instance.DoCaptureEvent(@event);
    }

    private Coroutine DoCaptureMessage(string message)
    {
        if (Debug)
        {
            UnityDebug.Log("sending message to sentry.");
        }

        var @event = new SentryEvent(message, GetBreadcrumbs())
        {
            level = "info"
        };

        return DoCaptureEvent(@event);
    }

    private Coroutine DoCaptureEvent(SentryEvent @event)
    {
        if (Debug)
        {
            UnityDebug.Log("sending event to sentry.");
        }

        return StartCoroutine(ContinueSendingEvent(@event));
    }

    private void DoAddBreadcrumb(string message)
    {
        if (!_initialized)
        {
            UnityDebug.LogError("Cannot AddBreadcrumb if we are not initialized");
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH\\:mm\\:ss");
        _breadcrumbs[_lastBreadcrumbPos] = new Breadcrumb(timestamp, message);
        _lastBreadcrumbPos += 1;
        _lastBreadcrumbPos %= Breadcrumb.MaxBreadcrumbs;
        if (_noBreadcrumbs < Breadcrumb.MaxBreadcrumbs)
        {
            _noBreadcrumbs += 1;
        }
    }

    private List<Breadcrumb> GetBreadcrumbs()
    {
        return Breadcrumb.CombineBreadcrumbs(_breadcrumbs,
            _lastBreadcrumbPos,
            _noBreadcrumbs);
    }

    public void OnEnable()
    {
        Application.logMessageReceived += OnLogMessageReceived;
    }

    public void OnDisable()
    {
        Application.logMessageReceived -= OnLogMessageReceived;
    }

    public void OnGUI()
    {
        if (_lastErrorMessage != "" && Debug)
        {
            GUILayout.TextArea(_lastErrorMessage);
            if (GUILayout.Button("Clear"))
            {
                _lastErrorMessage = "";
            }
        }
    }

    public void ScheduleException(string condition, string stackTrace)
    {
        if (Debug)
        {
            UnityDebug.Log("sending exception to sentry.");
        }

        var stack = new List<StackTraceSpec>();
        var exc = condition.Split(new char[] { ':' }, 2);
        if (exc.Length < 2)
        {
            UnityDebug.LogWarning("Sentry exception condition not valid");
            return;
        }
        var excType = exc[0];
        var excValue = exc[1].Substring(1); // strip the space

        foreach (var stackTraceSpec in GetStackTraces(stackTrace))
        {
            stack.Add(stackTraceSpec);
        }

        var @event = new SentryExceptionEvent(excType, excValue, GetBreadcrumbs(), stack);

        StartCoroutine(ContinueSendingEvent(@event));
    }

    private static IEnumerable<StackTraceSpec> GetStackTraces(string stackTrace)
    {
        var stackList = stackTrace.Split('\n');
        // the format is as follows:
        // Module.Class.Method[.Invoke] (arguments) (at filename:lineno)
        // where :lineno is optional, will be omitted in builds
        for (var i = stackList.Length - 1; i >= 0; i--)
        {
            string functionName;
            string filename;
            int lineNo;

            var item = stackList[i];
            if (item == string.Empty)
            {
                continue;
            }
            var closingParen = item.IndexOf(')');

            if (closingParen == -1)
            {
                continue;
            }
            try
            {
                functionName = item.Substring(0, closingParen + 1);
                if (item.Length < closingParen + 6)
                {
                    // No location and no params provided. Use it as-is
                    filename = string.Empty;
                    lineNo = -1;
                }
                else if (item.Substring(closingParen + 1, 5) != " (at ")
                {
                    // we did something wrong, failed the check
                    UnityDebug.Log("failed parsing " + item);
                    functionName = item;
                    lineNo = -1;
                    filename = string.Empty;
                }
                else
                {
                    var colon = item.LastIndexOf(':', item.Length - 1, item.Length - closingParen);
                    if (closingParen == item.Length - 1)
                    {
                        filename = string.Empty;
                        lineNo = -1;
                    }
                    else if (colon == -1)
                    {
                        filename = item.Substring(closingParen + 6, item.Length - closingParen - 7);
                        lineNo = -1;
                    }
                    else
                    {
                        filename = item.Substring(closingParen + 6, colon - closingParen - 6);
                        lineNo = Convert.ToInt32(item.Substring(colon + 1, item.Length - 2 - colon));
                    }
                }
            }
            catch
            {
                continue;
            }

            bool inApp;

            if (filename == string.Empty
                // i.e: <d315a7230dee4fa58154dc9e8884174d>
                || (filename[0] == '<' && filename[filename.Length - 1] == '>'))
            {
                // Addresses will mess with grouping. Unless possible to symbolicate, better not to report it.
                filename = string.Empty;
                inApp = true; // defaults to true

                if (functionName.Contains("UnityEngine."))
                {
                    inApp = false;
                }
            }
            else
            {
                inApp = filename.Contains("Assets/");
            }

            yield return new StackTraceSpec(filename, functionName, lineNo, inApp);
        }
    }

    public void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        if (!_initialized)
        {
            return; // dsn not initialized or something exploded, don't try to send it
        }
        _lastErrorMessage = condition;
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
        {
            if (AutoGenerateBreadcrumb) // add non-errors to the breadcrumb list
            {
                AddBreadcrumb(condition);
            }

            // only send errors, can be set somewhere what we send and what we don't
            return;
        }

        if (Time.time - _timeLastError <= MinTime)
        {
            return; // silently drop the event on the floor
        }
        _timeLastError = Time.time;
        if (type == LogType.Exception)
        {
            ScheduleException(condition, stackTrace);
        }
        else
        {
#if NET_4_6
            string message = $"{type.ToString()}: {condition}";
#else
            string message = type.ToString() + ": " + condition;
#endif
            ScheduleException(message, stackTrace);
        }
    }

    private void PrepareEvent(SentryEvent @event)
    {
        if (Version != "") // version override
        {
            @event.release = Version;
        }

        if (SendDefaultPii)
        {
            @event.contexts.device.name = SystemInfo.deviceName;
        }

        @event.tags.deviceUniqueIdentifier = SystemInfo.deviceUniqueIdentifier;
        @event.extra.unityVersion = Application.unityVersion;
        @event.extra.screenOrientation = Screen.orientation.ToString();
    }

    private StringBuilder _stringBuilder = new StringBuilder();
    private IEnumerator<UnityWebRequestAsyncOperation> ContinueSendingEvent<T>(T @event) where T : SentryEvent
    {
        PrepareEvent(@event);

        var s = JsonUtility.ToJson(@event);

        var sentryKey = _dsn.publicKey;
        var sentrySecret = _dsn.secretKey;

        _stringBuilder.Clear();
        _stringBuilder.Append("Sentry sentry_version=5,sentry_client=Unity0.1,");
        _stringBuilder.Append("sentry_timestamp=");
        _stringBuilder.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH\\:mm\\:ss"));
        _stringBuilder.Append(",sentry_key=");
        _stringBuilder.Append(sentryKey);
        _stringBuilder.Append(",sentry_secret=");
        _stringBuilder.Append(sentrySecret);

        var www = new UnityWebRequest(_dsn.callUri.ToString());
        www.method = "POST";
        www.SetRequestHeader("X-Sentry-Auth", _stringBuilder.ToString());
        www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(s));
        www.downloadHandler = new DownloadHandlerBuffer();
        yield return www.SendWebRequest();

        while (!www.isDone)
        {
            yield return null;
        }
        if (www.result == UnityWebRequest.Result.ConnectionError
            || www.result == UnityWebRequest.Result.ProtocolError
            || www.responseCode != 200)
        {
            UnityDebug.LogWarning("error sending request to sentry: " + www.error);
        }
        else if (Debug)
        {
            UnityDebug.Log("Sentry sent back: " + www.downloadHandler.text);
        }
    }
}
