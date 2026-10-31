using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeTrail {
    // One controller owns the state; this file contains its diagnostics responsibilities.
    internal sealed partial class Controller {
        void Trace(string message) {
            if (!Environment.GetCommandLineArgs().Contains("--trace")) return;
            try { File.AppendAllText(Path.Combine(disk.Folder, "capture-flow.txt"), DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine); } catch {}
        }
    }
}
