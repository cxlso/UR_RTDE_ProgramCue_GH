// Grasshopper Script Instance
#region Usings
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
#endregion

public class Script_Instance : GH_ScriptInstance
{
  const int RTDE_PORT = 30004;

  // RTDE package types
  const byte RTDE_REQUEST_PROTOCOL_VERSION        = 86; // 'V'
  const byte RTDE_CONTROL_PACKAGE_SETUP_OUTPUTS   = 79; // 'O'
  const byte RTDE_CONTROL_PACKAGE_SETUP_INPUTS    = 73; // 'I'
  const byte RTDE_CONTROL_PACKAGE_START           = 83; // 'S'
  const byte RTDE_DATA_PACKAGE                    = 85; // 'U'
  const byte RTDE_TEXT_MESSAGE                    = 77; // 'M'

  // UR analog out range (0..10V -> ratio 0..1)
  const double AO_VOLT_FULL_SCALE = 10.0;

  // Fixed burst behavior (not exposed)
  const int BURST_COUNT = 1;
  const int BURST_DELAY_MS = 0;

  // Edge detection
  bool prevReset  = false;
  bool prevResume = false;

  // Persistent log/state
  List<string> persistentLog = new List<string>();
  List<string> lastStatus = new List<string> { "AO0: idle", "AO1: idle" };

  // Cached DIGITAL (unchanged)
  bool hasDigitalCache = false;
  byte cachedStdDO  = 0;   // DO0..7
  byte cachedToolDO = 0;   // ToolDO0..1

  // Cached ANALOG (tracked from AnalogState strings)
  bool hasAO0 = false;
  bool hasAO1 = false;

  double cachedAO0Volts = 0.0;
  double cachedAO1Volts = 0.0;

  double cachedAO0Ratio = 0.0; // 0..1
  double cachedAO1Ratio = 0.0; // 0..1

  private void RunScript(
		string IP,
		bool Reset,
		bool Resume,
		object AnalogState,
		ref object Status,
		ref object Log)
  {
    bool resetEdge  = Reset && !prevReset;
    bool resumeEdge = Resume && !prevResume;

    if (string.IsNullOrWhiteSpace(IP))
    {
      lastStatus = BuildStatus("IP not set.");
      Status = new List<string>(lastStatus);
      Log = new List<string>(persistentLog);
      prevReset = Reset; prevResume = Resume;
      return;
    }

    // Block computation unless a toggle edge happened
    if (!resetEdge && !resumeEdge)
    {
      Status = new List<string>(lastStatus);
      Log = new List<string>(persistentLog);
      prevReset = Reset; prevResume = Resume;
      return;
    }

    try
    {
      if (resetEdge)
      {
        int found = ParseAndCacheAnalog(AnalogState);

        if (found == 0)
          Add("RESET edge -> WARNING: no analog lines parsed (expect e.g. 'standard_analog_output0: 2.5').");

        Add($"RESET edge -> snapshot DIGITAL + force tracked AO to 0V (force voltage mode). Tracked: AO0={(hasAO0 ? cachedAO0Volts.ToString("0.###") + "V" : "-")}, AO1={(hasAO1 ? cachedAO1Volts.ToString("0.###") + "V" : "-")}");

        DoRtdeTransaction(
          ip: IP,
          snapshotDigital: true,
          restoreDigital: false,
          forceVoltage: true,
          setAnalogToZero: true,
          restoreAnalogFromCache: false
        );

        lastStatus = BuildStatus("Reset sent (DO->0, AO->0V).");
      }

      if (resumeEdge)
      {
        if (!hasDigitalCache)
          Add("RESUME edge -> WARNING: no cached DIGITAL snapshot (press Reset once first). Will still restore analog if cached.");

        if (!hasAO0 && !hasAO1)
          Add("RESUME edge -> WARNING: no cached analog channels (nothing to restore).");

        Add($"RESUME edge -> restore DIGITAL + restore tracked AO from cache (force voltage mode). AO0={(hasAO0 ? cachedAO0Volts.ToString("0.###") + "V" : "-")}, AO1={(hasAO1 ? cachedAO1Volts.ToString("0.###") + "V" : "-")}");

        DoRtdeTransaction(
          ip: IP,
          snapshotDigital: false,
          restoreDigital: true,
          forceVoltage: true,
          setAnalogToZero: false,
          restoreAnalogFromCache: true
        );

        lastStatus = BuildStatus("Resume sent (restore cached).");
      }
    }
    catch (Exception ex)
    {
      Add("ERROR: " + ex.Message);
      Add(ex.ToString());
      lastStatus = BuildStatus("ERROR: " + ex.Message);
    }

    Status = new List<string>(lastStatus);
    Log = new List<string>(persistentLog);

    prevReset = Reset;
    prevResume = Resume;
  }

  List<string> BuildStatus(string suffix)
  {
    string s0 = hasAO0
      ? $"AO0: cached {cachedAO0Volts:0.###} V (ratio {cachedAO0Ratio:0.###}) | {suffix}"
      : $"AO0: not tracked | {suffix}";

    string s1 = hasAO1
      ? $"AO1: cached {cachedAO1Volts:0.###} V (ratio {cachedAO1Ratio:0.###}) | {suffix}"
      : $"AO1: not tracked | {suffix}";

    return new List<string> { s0, s1 };
  }

  // ---------------- Core RTDE transaction ----------------

  void DoRtdeTransaction(string ip, bool snapshotDigital, bool restoreDigital, bool forceVoltage, bool setAnalogToZero, bool restoreAnalogFromCache)
  {
    using (var client = new TcpClient())
    {
      client.NoDelay = true;
      client.ReceiveTimeout = 2500;
      client.SendTimeout = 2500;
      client.Connect(ip, RTDE_PORT);

      using (var stream = client.GetStream())
      {
        RequestProtocolVersion(stream, 2);

        // OUTPUT recipe: minimal
        SetupOutputs(stream, 125.0, new string[]
        {
          "runtime_state",
          "actual_digital_output_bits"
        });

        // INPUT recipe: DO + AO0 + AO1 + type
        var inputFields = new string[]
        {
          "standard_digital_output_mask",
          "standard_digital_output",
          "tool_digital_output_mask",
          "tool_digital_output",
          "standard_analog_output_mask",
          "standard_analog_output_type",
          "standard_analog_output_0",
          "standard_analog_output_1"
        };

        byte inputRecipeId = SetupInputs(stream, inputFields);
        Add("RTDE input recipe id = " + inputRecipeId);

        StartRtde(stream);

        // Snapshot digital outputs (unchanged)
        if (snapshotDigital)
        {
          var snap = ReadOneDataPackage(stream);
          if (snap.hasData)
          {
            ulong bits = snap.actualDigitalOutputBits;
            cachedStdDO  = (byte)(bits & 0xFF);
            cachedToolDO = (byte)((bits >> 16) & 0x03);
            hasDigitalCache = true;
            Add($"Snapshot DIGITAL: stdDO=0x{cachedStdDO:X2}, toolDO=0x{cachedToolDO:X2}");
          }
          else
          {
            hasDigitalCache = false;
            Add("Snapshot DIGITAL FAILED: did not receive RTDE data package.");
          }
        }

        // Digital values
        byte stdDoMask  = 0xFF;
        byte toolDoMask = 0x03;

        byte stdDoVal, toolDoVal;
        if (restoreDigital && hasDigitalCache)
        {
          stdDoVal  = cachedStdDO;
          toolDoVal = cachedToolDO;
        }
        else
        {
          stdDoVal  = 0x00;
          toolDoVal = 0x00;
        }

        // Analog mask/type/value
        byte aoMask = 0x00;
        if (hasAO0) aoMask |= 0x01;
        if (hasAO1) aoMask |= 0x02;

        byte aoType = 0x00;
        if (forceVoltage)
        {
          if (hasAO0) aoType |= 0x01;
          if (hasAO1) aoType |= 0x02;
        }

        double ao0 = 0.0;
        double ao1 = 0.0;

        if (restoreAnalogFromCache)
        {
          ao0 = hasAO0 ? cachedAO0Ratio : 0.0;
          ao1 = hasAO1 ? cachedAO1Ratio : 0.0;
        }
        else if (setAnalogToZero)
        {
          ao0 = 0.0;
          ao1 = 0.0;
        }

        ao0 = Clamp01(ao0);
        ao1 = Clamp01(ao1);

        Add($"Sending {BURST_COUNT} packet(s): stdDO=0x{stdDoVal:X2}, toolDO=0x{toolDoVal:X2}, AOmask=0x{aoMask:X2}, AOtype=0x{aoType:X2}, AO0={ao0:0.###}, AO1={ao1:0.###}");

        for (int i = 0; i < BURST_COUNT; i++)
        {
          SendInputData(stream, inputRecipeId,
            stdDoMask, stdDoVal,
            toolDoMask, toolDoVal,
            aoMask, aoType,
            ao0, ao1);

          DrainSome(stream, 1);
          if (BURST_DELAY_MS > 0) Thread.Sleep(BURST_DELAY_MS);
        }

        Add("RTDE transaction done.");
      }
    }
  }

  // ---------------- Parse and cache analog strings ----------------

  int ParseAndCacheAnalog(object analogState)
  {
    // Reset tracking flags on each Reset edge
    hasAO0 = false;
    hasAO1 = false;

    int count = 0;

    foreach (string line in EnumerateStrings(analogState))
    {
      if (string.IsNullOrWhiteSpace(line)) continue;

      string s = line.Trim();
      string lower = s.ToLowerInvariant();

      int ch = -1;
      if (lower.StartsWith("standard_analog_output0")) ch = 0;
      else if (lower.StartsWith("standard_analog_output1")) ch = 1;
      else continue;

      int colon = s.IndexOf(':');
      if (colon < 0) continue;

      string rhs = s.Substring(colon + 1).Trim();

      if (!double.TryParse(rhs, NumberStyles.Any, CultureInfo.InvariantCulture, out double volts))
      {
        if (!double.TryParse(rhs, out volts))
          continue;
      }

      double ratio = VoltsToRatio(volts);

      if (ch == 0)
      {
        hasAO0 = true;
        cachedAO0Volts = volts;
        cachedAO0Ratio = ratio;
        Add($"Parsed AO0: {cachedAO0Volts:0.###} V -> ratio {cachedAO0Ratio:0.###}");
        count++;
      }
      else
      {
        hasAO1 = true;
        cachedAO1Volts = volts;
        cachedAO1Ratio = ratio;
        Add($"Parsed AO1: {cachedAO1Volts:0.###} V -> ratio {cachedAO1Ratio:0.###}");
        count++;
      }
    }

    return count;
  }

  IEnumerable<string> EnumerateStrings(object x)
  {
    if (x == null) yield break;

    if (x is IGH_Goo goo)
    {
      object sv = goo.ScriptVariable();
      if (sv != null && !(sv is IGH_Goo))
      {
        foreach (var s in EnumerateStrings(sv)) yield return s;
      }
      else
      {
        yield return goo.ToString();
      }
      yield break;
    }

    if (x is string one)
    {
      yield return one;
      yield break;
    }

    if (x is IEnumerable en && !(x is string))
    {
      foreach (var item in en)
      {
        foreach (var s in EnumerateStrings(item)) yield return s;
      }
      yield break;
    }

    yield return x.ToString();
  }

  double VoltsToRatio(double volts)
  {
    if (double.IsNaN(volts)) volts = 0.0;
    return Clamp01(volts / AO_VOLT_FULL_SCALE);
  }

  double Clamp01(double x)
  {
    if (double.IsNaN(x)) return 0.0;
    if (x < 0.0) return 0.0;
    if (x > 1.0) return 1.0;
    return x;
  }

  // ---------------- RTDE protocol ----------------

  void RequestProtocolVersion(NetworkStream stream, ushort version)
  {
    var payload = new byte[2];
    WriteUInt16BE(payload, 0, version);
    WritePackage(stream, RTDE_REQUEST_PROTOCOL_VERSION, payload);

    var resp = ReadPackage(stream);
    if (resp.type != RTDE_REQUEST_PROTOCOL_VERSION) throw new Exception("RTDE: invalid protocol version response type");
    if (resp.payload.Length < 1) throw new Exception("RTDE: protocol version response too short");

    bool accepted = resp.payload[0] == 1;
    Add("RTDE protocol v" + version + " -> " + (accepted ? "accepted" : "denied"));
    if (!accepted) throw new Exception("RTDE protocol version denied. Try version 1.");
  }

  void SetupOutputs(NetworkStream stream, double frequencyHz, string[] outputs)
  {
    byte[] vars = Encoding.ASCII.GetBytes(string.Join(",", outputs));
    byte[] payload = new byte[8 + vars.Length];
    WriteDoubleBE(payload, 0, frequencyHz);
    Buffer.BlockCopy(vars, 0, payload, 8, vars.Length);

    WritePackage(stream, RTDE_CONTROL_PACKAGE_SETUP_OUTPUTS, payload);

    var resp = ReadPackage(stream);
    if (resp.type != RTDE_CONTROL_PACKAGE_SETUP_OUTPUTS) throw new Exception("RTDE: invalid setup outputs response type");

    string types = Encoding.ASCII.GetString(resp.payload, 0, resp.payload.Length);
    Add("RTDE setup outputs types: " + types);
    if (types.Contains("NOT_FOUND")) throw new Exception("RTDE: one or more output fields NOT_FOUND");
  }

  byte SetupInputs(NetworkStream stream, string[] fields)
  {
    byte[] payload = Encoding.ASCII.GetBytes(string.Join(",", fields));
    WritePackage(stream, RTDE_CONTROL_PACKAGE_SETUP_INPUTS, payload);

    var resp = ReadPackage(stream);
    if (resp.type != RTDE_CONTROL_PACKAGE_SETUP_INPUTS) throw new Exception("RTDE: invalid setup inputs response type");
    if (resp.payload.Length < 1) throw new Exception("RTDE: setup inputs response too short");

    byte recipeId = resp.payload[0];
    string types = resp.payload.Length > 1 ? Encoding.ASCII.GetString(resp.payload, 1, resp.payload.Length - 1) : "";
    Add("RTDE setup inputs types: " + types);

    if (recipeId == 0) throw new Exception("RTDE: got recipe_id 0 (setup failed?)");
    if (types.Contains("NOT_FOUND")) throw new Exception("RTDE: one or more input fields NOT_FOUND");

    return recipeId;
  }

  void StartRtde(NetworkStream stream)
  {
    WritePackage(stream, RTDE_CONTROL_PACKAGE_START, new byte[0]);
    var resp = ReadPackage(stream);
    if (resp.type != RTDE_CONTROL_PACKAGE_START) throw new Exception("RTDE: invalid start response type");
    if (resp.payload.Length < 1) throw new Exception("RTDE: start response too short");

    bool accepted = resp.payload[0] == 1;
    Add("RTDE start -> " + (accepted ? "accepted" : "denied"));
    if (!accepted) throw new Exception("RTDE start denied");
  }

  void SendInputData(NetworkStream stream, byte recipeId,
                     byte stdDoMask, byte stdDoVal,
                     byte toolDoMask, byte toolDoVal,
                     byte aoMask, byte aoType,
                     double ao0, double ao1)
  {
    var payload = new byte[1 + 6 + 8 + 8];
    int p = 0;

    payload[p++] = recipeId;

    payload[p++] = stdDoMask;
    payload[p++] = stdDoVal;
    payload[p++] = toolDoMask;
    payload[p++] = toolDoVal;
    payload[p++] = aoMask;
    payload[p++] = aoType;

    WriteDoubleBE(payload, p, ao0); p += 8;
    WriteDoubleBE(payload, p, ao1);

    WritePackage(stream, RTDE_DATA_PACKAGE, payload);
  }

  (bool hasData, uint runtimeState, ulong actualDigitalOutputBits) ReadOneDataPackage(NetworkStream stream)
  {
    int tries = 0;
    while (tries < 60)
    {
      if (!stream.DataAvailable)
      {
        Thread.Sleep(10);
        tries++;
        continue;
      }

      var resp = ReadPackage(stream);

      if (resp.type == RTDE_TEXT_MESSAGE)
      {
        string msg = Encoding.ASCII.GetString(resp.payload, 0, resp.payload.Length);
        Add("RTDE msg: " + msg);
        continue;
      }

      if (resp.type != RTDE_DATA_PACKAGE) continue;
      if (resp.payload.Length < 1 + 4 + 8) continue;

      int p = 1; // skip recipe id
      uint rs = ReadUInt32BE(resp.payload, p); p += 4;
      ulong bits = ReadUInt64BE(resp.payload, p);

      return (true, rs, bits);
    }

    return (false, 0u, 0ul);
  }

  void DrainSome(NetworkStream stream, int packets)
  {
    for (int i = 0; i < packets; i++)
    {
      if (!stream.DataAvailable) return;

      var resp = ReadPackage(stream);
      if (resp.type == RTDE_TEXT_MESSAGE)
      {
        string msg = Encoding.ASCII.GetString(resp.payload, 0, resp.payload.Length);
        Add("RTDE msg: " + msg);
      }
    }
  }

  // ---------------- Packet IO ----------------

  void WritePackage(NetworkStream stream, byte type, byte[] payload)
  {
    int length = 3 + payload.Length;
    var buf = new byte[length];
    WriteUInt16BE(buf, 0, (ushort)length);
    buf[2] = type;
    Buffer.BlockCopy(payload, 0, buf, 3, payload.Length);
    stream.Write(buf, 0, buf.Length);
  }

  (byte type, byte[] payload) ReadPackage(NetworkStream stream)
  {
    byte[] header = ReadExact(stream, 3);
    ushort length = ReadUInt16BE(header, 0);
    byte type = header[2];

    int payloadLen = length - 3;
    byte[] payload = payloadLen > 0 ? ReadExact(stream, payloadLen) : new byte[0];
    return (type, payload);
  }

  byte[] ReadExact(NetworkStream stream, int n)
  {
    byte[] buf = new byte[n];
    int read = 0;
    while (read < n)
    {
      int r = stream.Read(buf, read, n - read);
      if (r <= 0) throw new Exception("RTDE: controller stopped sending data");
      read += r;
    }
    return buf;
  }

  // ---------------- Big-endian helpers ----------------

  void WriteUInt16BE(byte[] buf, int offset, ushort v)
  {
    buf[offset + 0] = (byte)((v >> 8) & 0xFF);
    buf[offset + 1] = (byte)(v & 0xFF);
  }

  ushort ReadUInt16BE(byte[] buf, int offset)
  {
    return (ushort)((buf[offset] << 8) | buf[offset + 1]);
  }

  uint ReadUInt32BE(byte[] buf, int offset)
  {
    return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
  }

  ulong ReadUInt64BE(byte[] buf, int offset)
  {
    ulong v = 0;
    for (int i = 0; i < 8; i++) v = (v << 8) | buf[offset + i];
    return v;
  }

  void WriteDoubleBE(byte[] buf, int offset, double v)
  {
    byte[] tmp = BitConverter.GetBytes(v);
    if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
    Buffer.BlockCopy(tmp, 0, buf, offset, 8);
  }

  // ---------------- Logging ----------------

  void Add(string msg)
  {
    persistentLog.Insert(0, $"{DateTime.Now:HH:mm:ss} {msg}");
    if (persistentLog.Count > 1000) persistentLog.RemoveRange(1000, persistentLog.Count - 1000);
  }
}