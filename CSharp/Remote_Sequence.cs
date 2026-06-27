// Grasshopper Script Instance
#region Usings
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
#endregion

public class Script_Instance : GH_ScriptInstance
{
  // Ports
  const int SECONDARY_PORT = 30002;
  const int DASHBOARD_PORT = 29999;

  // Your mapping: runtime_state == 1 means STOPPED
  const int STOPPED_VALUE = 1;

  // Timing
  const int CONNECT_TIMEOUT_MS = 1500;
  const int POLL_MS = 40;                 // RTDE polling gate
  const int START_TIMEOUT_MS = 3000;      // wait for "not stopped" after send
  const int STOP_SETTLE_TIMEOUT_MS = 5000;
  const int POST_STOP_SETTLE_MS = 200;

  // Debounce: require "stopped" stable before sending next
  const int STOPPED_STABLE_MS = 200;

  // Retry behavior for flaky starts
  const int START_RETRIES = 2;            // after first send, retry up to 2 times
  const int RETRY_BACKOFF_MS = 250;

  // FreeDrive handshake
  const int FREEDRIVE_WAIT_NOTRUNNING_MS = 2000;
  const int FREEDRIVE_WAIT_RUNNING_MS = 1200;

  // Edge memory
  bool prevUpload = false;
  bool prevPause  = false;
  bool prevResume = false;
  bool prevFreeDrive = false;

  // Persistent log + UI state
  List<string> persistentLog = new List<string>();
  string lastStatus = "Idle.";
  int lastIndex = -1;
  string lastName = "";

  // Sequencer state
  Thread worker = null;
  volatile bool seqRunning = false;
  volatile bool seqPauseRequested = false;
  volatile bool seqStopRequested = false;

  volatile bool seqFailed = false;
  volatile string seqFailReason = "";

  // Live RTDE state (updated by RunScript each GH solution)
  volatile int latestRuntimeState = STOPPED_VALUE;

  // Snapshot of programs for the running sequence
  List<object> seqPrograms = null;

  // FreeDrive state
  volatile bool freeDriveActive = false;

  private void RunScript(
		string IP,
		List<object> Programs,
		int RuntimeState,
		bool Upload,
		bool Pause,
		bool Resume,
		bool FreeDrive,
		ref object Status,
		ref object Index,
		ref object ID,
		ref object Log)
  {
    // Always update latest RTDE state (worker watches this)
    latestRuntimeState = RuntimeState;

    bool uploadEdge = Upload && !prevUpload;
    bool pauseEdge  = Pause && !prevPause;
    bool resumeEdge = Resume && !prevResume;

    bool freeDriveOnEdge  = FreeDrive && !prevFreeDrive;
    bool freeDriveOffEdge = !FreeDrive && prevFreeDrive;

    if (string.IsNullOrWhiteSpace(IP))
    {
      lastStatus = "IP not set.";
      SetOutputs(ref Status, ref Index, ref ID, ref Log);
      prevUpload = Upload; prevPause = Pause; prevResume = Resume; prevFreeDrive = FreeDrive;
      return;
    }

    // -------------------- FreeDrive toggle handling (robust handshake) --------------------
    try
    {
      if (freeDriveOnEdge)
      {
        Add("FREEDRIVE ON edge -> start freedrive holder program (with handshake)");
        freeDriveActive = true;

        // Best-effort: stop any sequencer thread
        if (seqRunning)
        {
          Add("FREEDRIVE -> stopping existing sequence...");
          seqStopRequested = true;
        }

        // Best-effort pause whatever is running (safer)
        try
        {
          SendSecondaryRobust(IP, "pause program");
          Add("FREEDRIVE -> pause program sent");
        }
        catch (Exception ex)
        {
          Add("FREEDRIVE -> pause program failed (continuing): " + ex.Message);
        }

        // Wait until dashboard says running=false (settle), but don't hard-fail if it times out
        bool becameNotRunning = WaitDashboardRunning(IP, false, FREEDRIVE_WAIT_NOTRUNNING_MS);
        Add("FREEDRIVE -> wait running=false : " + (becameNotRunning ? "OK" : "timeout"));

        Thread.Sleep(120);

        // Freedrive must be kept alive by a running program: send a holder loop
        string fdScript =
          "def GH_FREEDRIVE():\n" +
          "  freedrive_mode()\n" +
          "  while (True):\n" +
          "    sync()\n" +
          "  end\n" +
          "end\n" +
          "GH_FREEDRIVE()\n";

        SendSecondaryRobust(IP, fdScript);
        Add("FREEDRIVE -> holder sent (attempt 1)");

        // Verify it actually started; if not, resend once
        bool started = WaitDashboardRunning(IP, true, FREEDRIVE_WAIT_RUNNING_MS);
        if (!started)
        {
          Add("FREEDRIVE -> holder did not start, resending once...");
          Thread.Sleep(180);
          SendSecondaryRobust(IP, fdScript);
          Add("FREEDRIVE -> holder sent (attempt 2)");
          started = WaitDashboardRunning(IP, true, FREEDRIVE_WAIT_RUNNING_MS);
        }

        lastStatus = started
          ? "FreeDrive enabled (holder running). Toggle OFF to exit."
          : "WARNING: FreeDrive holder may not have started (check Remote / safety / pendant).";

        RequestRecompute();
      }

      if (freeDriveOffEdge)
      {
        Add("FREEDRIVE OFF edge -> stop freedrive holder");
        freeDriveActive = false;

        // Stop the holder loop program
        TryDashboard(IP, "stop", out string stopResp);
        if (!string.IsNullOrWhiteSpace(stopResp))
          Add("dashboard stop -> " + stopResp.Trim());

        // Wait for running=false (settle)
        bool becameNotRunning = WaitDashboardRunning(IP, false, 2000);
        Add("FREEDRIVE -> wait running=false after stop : " + (becameNotRunning ? "OK" : "timeout"));

        // Best-effort cleanup
        try { SendSecondaryRobust(IP, "end_freedrive_mode()"); }
        catch { /* ignore */ }

        lastStatus = "FreeDrive disabled.";
        Add("FREEDRIVE -> disabled");
        RequestRecompute();
      }
    }
    catch (Exception ex)
    {
      Add(ex.ToString());
      lastStatus = "ERROR (FreeDrive): " + ex.Message;
      RequestRecompute();
    }

    // While FreeDrive active, block sequencing controls (avoid fighting the holder loop)
    if (freeDriveActive)
    {
      if (uploadEdge) Add("UPLOAD edge ignored because FreeDrive is ON.");
      if (pauseEdge)  Add("PAUSE edge ignored because FreeDrive is ON.");
      if (resumeEdge) Add("RESUME edge ignored because FreeDrive is ON.");

      SetOutputs(ref Status, ref Index, ref ID, ref Log);

      prevUpload = Upload;
      prevPause  = Pause;
      prevResume = Resume;
      prevFreeDrive = FreeDrive;
      return;
    }
    // ----------------------------------------------------------------------

    // Pause/Resume can be sent anytime (when not in FreeDrive)
    try
    {
      if (pauseEdge)
      {
        Add("PAUSE edge -> pause program");
        seqPauseRequested = true;
        SendSecondaryRobust(IP, "pause program");
        lastStatus = "Pause sent.";
        RequestRecompute();
      }

      if (resumeEdge)
      {
        Add("RESUME edge -> resume program");
        seqPauseRequested = false;
        SendSecondaryRobust(IP, "resume program");
        lastStatus = "Resume sent.";
        RequestRecompute();
      }
    }
    catch (Exception ex)
    {
      Add(ex.ToString());
      lastStatus = "ERROR: " + ex.Message;
      RequestRecompute();
    }

    // Upload edge: (re)start sequence
    if (uploadEdge)
    {
      try
      {
        if (Programs == null || Programs.Count == 0)
          throw new Exception("Programs list is empty (provide a flattened list).");

        // Stop any existing worker
        if (seqRunning)
        {
          Add("UPLOAD edge -> stopping existing sequence...");
          seqStopRequested = true;
          TryDashboard(IP, "stop", out _);
        }

        // Reset run flags/UI state
        seqFailed = false;
        seqFailReason = "";
        lastIndex = -1;
        lastName = "";
        lastStatus = "Sequence starting...";
        RequestRecompute();

        // Snapshot list for this run
        seqPrograms = new List<object>(Programs);
        seqStopRequested = false;
        seqPauseRequested = false;

        // Start worker
        worker = new Thread(() => SequenceWorker(IP));
        worker.IsBackground = true;
        seqRunning = true;
        worker.Start();

        Add($"SEQ -> started ({seqPrograms.Count} program(s))");
        lastStatus = $"Sequence started ({seqPrograms.Count} program(s)).";
        RequestRecompute();
      }
      catch (Exception ex)
      {
        Add(ex.ToString());
        lastStatus = "ERROR: " + ex.Message;
        RequestRecompute();
      }
    }

    SetOutputs(ref Status, ref Index, ref ID, ref Log);

    prevUpload = Upload;
    prevPause  = Pause;
    prevResume = Resume;
    prevFreeDrive = FreeDrive;
  }

  // ---------------------------- Worker ----------------------------

  void SequenceWorker(string ip)
  {
    try
    {
      for (int i = 0; i < seqPrograms.Count; i++)
      {
        if (seqStopRequested) break;

        // Pause gate
        while (seqPauseRequested && !seqStopRequested)
          Thread.Sleep(POLL_MS);

        if (seqStopRequested) break;

        object pObj = seqPrograms[i];
        if (pObj == null)
        {
          Add($"SEQ -> Program[{i}] is null (skip)");
          continue;
        }

        dynamic prog = pObj;

        string name;
        List<string> lines;

        try
        {
          name = (string)prog.Name;
          lines = prog.Code[0][0];
        }
        catch
        {
          Add($"SEQ -> Program[{i}] invalid (missing Name/Code) (skip)");
          continue;
        }

        if (lines == null || lines.Count == 0)
        {
          Add($"SEQ -> Program[{i}] '{name}' empty Code (skip)");
          continue;
        }

        lastIndex = i;
        lastName = name;
        lastStatus = $"Preparing [{i + 1}/{seqPrograms.Count}] {name}";
        Add($"SEQ -> [{i}] prepare '{name}'");
        RequestRecompute();

        // If robot is running, stop it first; if already stopped, don't spam stop
        if (latestRuntimeState != STOPPED_VALUE)
        {
          TryDashboard(ip, "stop", out string stopResp);
          if (!string.IsNullOrWhiteSpace(stopResp))
            Add("dashboard stop -> " + stopResp.Trim());

          // Wait until RTDE confirms stopped
          int waited = 0;
          while (latestRuntimeState != STOPPED_VALUE && !seqStopRequested && waited < STOP_SETTLE_TIMEOUT_MS)
          {
            Thread.Sleep(POLL_MS);
            waited += POLL_MS;
          }
        }

        // Require STOPPED stable for a short window (debounce)
        if (!WaitUntilStoppedStable(STOPPED_STABLE_MS))
        {
          FailRun($"STOPPED did not become stable before sending '{name}'.");
          break;
        }

        Thread.Sleep(POST_STOP_SETTLE_MS);

        string script = string.Join("\n", lines);

        // Send + verify start (with retries + recovery)
        bool ok = SendAndConfirmStart(ip, name, script);
        if (!ok)
        {
          // fail reason already set inside
          break;
        }

        // Wait for finish: runtime_state returns to STOPPED
        lastStatus = $"Running [{i + 1}/{seqPrograms.Count}] {name}";
        RequestRecompute();

        WaitUntilRuntimeStopped();

        Add($"SEQ -> finished '{name}'");
        lastStatus = $"Finished [{i + 1}/{seqPrograms.Count}] {name}";
        RequestRecompute();
      }

      if (seqFailed)
      {
        Add("SEQ -> failed");
        // lastStatus already set by FailRun
      }
      else if (!seqStopRequested)
      {
        lastStatus = "Sequence complete.";
        Add("SEQ -> complete");
      }
      else
      {
        lastStatus = "Sequence stopped.";
        Add("SEQ -> stopped");
      }

      RequestRecompute();
    }
    catch (Exception ex)
    {
      Add(ex.ToString());
      FailRun("SEQ ERROR: " + ex.Message);
      RequestRecompute();
    }
    finally
    {
      seqRunning = false;
    }
  }

  bool SendAndConfirmStart(string ip, string name, string script)
  {
    for (int attempt = 0; attempt <= START_RETRIES; attempt++)
    {
      if (seqStopRequested) return false;

      lastStatus = $"Sending [{lastIndex + 1}/{seqPrograms.Count}] {name} (attempt {attempt + 1}/{START_RETRIES + 1})";
      Add($"SEQ -> send '{name}' (attempt {attempt + 1}/{START_RETRIES + 1})");
      RequestRecompute();

      SendSecondaryRobust(ip, script);

      lastStatus = $"Waiting start [{lastIndex + 1}/{seqPrograms.Count}] {name}";
      RequestRecompute();

      bool started = WaitUntilRuntimeNotStopped(START_TIMEOUT_MS);
      if (started) return true;

      // Not started: log dashboard state for diagnosis
      TryDashboard(ip, "robotmode", out string rm);
      TryDashboard(ip, "programState", out string ps);
      TryDashboard(ip, "running", out string rr);
      Add($"SEQ -> start FAIL '{name}' (rtde stayed {STOPPED_VALUE}). robotmode={rm.Trim()} programState={ps.Trim()} running={rr.Trim()}");

      // Recovery actions (best-effort)
      TryDashboard(ip, "close popup", out _);
      TryDashboard(ip, "unlock protective stop", out _);

      // Give the controller time to settle before retry
      Thread.Sleep(RETRY_BACKOFF_MS);

      // Ensure STOPPED stable before retrying
      WaitUntilStoppedStable(STOPPED_STABLE_MS);
    }

    FailRun($"'{name}' did not start after {START_RETRIES + 1} attempt(s).");
    return false;
  }

  bool WaitUntilStoppedStable(int stableMs)
  {
    int stable = 0;
    while (!seqStopRequested)
    {
      while (seqPauseRequested && !seqStopRequested)
        Thread.Sleep(POLL_MS);

      if (seqStopRequested) return false;

      if (latestRuntimeState == STOPPED_VALUE)
      {
        stable += POLL_MS;
        if (stable >= stableMs) return true;
      }
      else
      {
        stable = 0;
      }

      Thread.Sleep(POLL_MS);
    }
    return false;
  }

  bool WaitUntilRuntimeNotStopped(int timeoutMs)
  {
    int waited = 0;
    while (!seqStopRequested && waited < timeoutMs)
    {
      while (seqPauseRequested && !seqStopRequested)
        Thread.Sleep(POLL_MS);

      if (seqStopRequested) return false;

      if (latestRuntimeState != STOPPED_VALUE)
        return true;

      Thread.Sleep(POLL_MS);
      waited += POLL_MS;
    }
    return false;
  }

  void WaitUntilRuntimeStopped()
  {
    while (!seqStopRequested)
    {
      while (seqPauseRequested && !seqStopRequested)
        Thread.Sleep(POLL_MS);

      if (seqStopRequested) return;

      if (latestRuntimeState == STOPPED_VALUE)
        return;

      Thread.Sleep(POLL_MS);
    }
  }

  void FailRun(string reason)
  {
    seqFailed = true;
    seqFailReason = reason ?? "";
    lastStatus = "SEQUENCE FAILED: " + seqFailReason;
    Add(lastStatus);
    RequestRecompute();
  }

  // -------------------- GH UI refresh --------------------

  void RequestRecompute()
  {
    if (GrasshopperDocument != null && Component != null)
      GrasshopperDocument.ScheduleSolution(1, doc => Component.ExpireSolution(false));
  }

  // -------------------- Networking --------------------

  void SendSecondaryRobust(string ip, string message)
  {
    Exception last = null;
    for (int i = 0; i < 3; i++)
    {
      try
      {
        SendSecondary(ip, message);
        return;
      }
      catch (Exception ex)
      {
        last = ex;
        Add($"30002 send failed ({i + 1}/3): {ex.Message}");
        Thread.Sleep(150);
      }
    }
    throw new Exception("30002 send failed after retries.", last);
  }

  void SendSecondary(string ip, string message)
  {
    using (var client = new TcpClient())
    {
      client.ReceiveTimeout = CONNECT_TIMEOUT_MS;
      client.SendTimeout = CONNECT_TIMEOUT_MS;
      client.Connect(ip, SECONDARY_PORT);

      using (var stream = client.GetStream())
      {
        if (!message.EndsWith("\n")) message += "\n";
        byte[] buf = Encoding.ASCII.GetBytes(message);
        stream.Write(buf, 0, buf.Length);
      }
    }
  }

  bool TryDashboard(string ip, string cmd, out string response)
  {
    response = "";
    try
    {
      using (var client = new TcpClient())
      {
        client.ReceiveTimeout = CONNECT_TIMEOUT_MS;
        client.SendTimeout = CONNECT_TIMEOUT_MS;
        client.Connect(ip, DASHBOARD_PORT);

        using (var stream = client.GetStream())
        {
          ReadLine(stream); // greeting
          byte[] buf = Encoding.ASCII.GetBytes(cmd + "\n");
          stream.Write(buf, 0, buf.Length);
          response = ReadLine(stream);
          return true;
        }
      }
    }
    catch { return false; }
  }

  string ReadLine(NetworkStream stream)
  {
    var sb = new StringBuilder();
    int start = Environment.TickCount;

    while (true)
    {
      if (stream.DataAvailable)
      {
        int b = stream.ReadByte();
        if (b < 0 || b == '\n') break;
        if (b != '\r') sb.Append((char)b);
      }
      else
      {
        Thread.Sleep(10);
        if (Environment.TickCount - start > CONNECT_TIMEOUT_MS) break;
      }
    }
    return sb.ToString();
  }

  // -------------------- FreeDrive handshake helpers --------------------

  bool IsDashboardRunningTrue(string resp)
  {
    if (resp == null) return false;
    resp = resp.ToLowerInvariant();
    return resp.Contains("true");
  }

  bool WaitDashboardRunning(string ip, bool wantRunning, int timeoutMs)
  {
    int waited = 0;
    const int step = 80;
    while (waited < timeoutMs)
    {
      if (TryDashboard(ip, "running", out string rr))
      {
        bool isRunning = IsDashboardRunningTrue(rr);
        if (isRunning == wantRunning) return true;
      }
      Thread.Sleep(step);
      waited += step;
    }
    return false;
  }

  // -------------------- Logging + outputs --------------------

  void Add(string msg)
  {
    persistentLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {msg}");
    if (persistentLog.Count > 2000) persistentLog.RemoveRange(2000, persistentLog.Count - 2000);
  }

  void SetOutputs(ref object Status, ref object Index, ref object ID, ref object Log)
  {
    Status = lastStatus;
    Index = lastIndex;
    ID = lastName;
    Log = new List<string>(persistentLog);
  }
}