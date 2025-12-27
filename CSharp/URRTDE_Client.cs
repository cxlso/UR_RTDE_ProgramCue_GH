/*
********************************************************************************
*  NOTICE
*
*  I am not the author of these scripts.
*  I found them online some time ago and cannot relocate the original source
*  (please let me know if you find it).
*  I believe they were written by Visose (https://github.com/visose),
*  the creator of the Robots plugin.
*  I only updated them to work inside the newer Grasshopper C# environment.
********************************************************************************
*/


// Grasshopper Script Instance
#region Usings

#r "C:\Users\celso\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\Robots\1.9.0\Robots.dll"
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Robots;
#endregion

public class Script_Instance : GH_ScriptInstance
{
    private void RunScript(string IP, List<string> N, ref object A, ref object B)
    {
    A = null;     // safe default
    B = null;     // (optional but clean)

    if(_ip != IP)
    {
        _ip = IP;
        Dispose();
    }

    try
    {
        if(_client == null)
        _client = new URRealTimeDataExchange(IP, N);

        _client.ReadOutputs();

        A = _client.Outputs
        .Select(o => $"{o.Name}: {string.Join(", ", o.Values.Select(v => v.ToString("0.###")))}")
        .ToList();

        B = _client.Log;
    }
    catch(Exception e)
    {
        Dispose();
        B = e.Message;
    }
    }


  // <Custom additional code> 
  string _ip;
  URRealTimeDataExchange _client = null;

  void Dispose()
  {
    if(_client != null)
      _client.Dispose();

    _client = null;
  }

  // </Custom additional code> 
}
