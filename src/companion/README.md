# x86 input companion

This small x86 process supplies the 32-bit side of the generic foreground input
bridge. It contains no application-name allowlist and does not inspect documents,
history files, clipboard contents, screen pixels or browser traffic. The parent
selects the current target process and its GUI threads automatically, validates
process/thread creation identities before reusing a subscription, and skips
metadata failures per process instead of tearing down every active subscription.
The helper is warmed before keyboard monitoring starts. Native readiness is
published only after the corresponding ordered acknowledgement; CLR `ready`
alone never means a target is being recorded.

Windows requires hook DLL bitness to match the target process and requires the
hook-owning thread to keep pumping messages. See Microsoft's
[SetWindowsHookExW contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw).
The existing x64 DLL therefore cannot provide this side of input capture.

Build:

```powershell
./build.ps1 -OutputDirectory C:\path\to\candidate
```

Deliver only `TypeTrail.InputHost.x86.exe` and
the versioned `TypeTrail.InputBridge.<revision>.x86.dll`, alongside the x64 controller. The build uses
the same native bridge and TSF observer sources as the main product. This version
has a distinct native DLL basename from the earlier product release, so disabled
older pinned modules do not prevent updating.

The child accepts `--parent PID`. The parent redirects stdin/stdout and hides the
console. Each UTF-8 line contains a JSON object. Input fields are case-sensitive:

```json
{"Op":"bootstrap","Exclusions":"ExcludedApp.exe"}
{"Op":"policy","Exclusions":"ExcludedApp.exe"}
{"Op":"start","Pids":[123],"Threads":[456]}
{"Op":"extend","Pids":[123,124],"Threads":[456,457]}
{"Op":"reconcile","Pids":[124],"Threads":[457]}
{"Op":"input","Pid":123,"Window":789,"At":123456789}
{"Op":"stats"}
{"Op":"flush","Id":1}
{"Op":"stop"}
{"Op":"exit"}
```

`input` forwards only real keyboard-activity metadata already observed by the
parent; it never creates synthetic activity or transfers raw key codes. Configuration
and lifecycle calls run on the child's STA WinForms message-pump thread. The stdin
reader parses and coalesces repeated activity metadata; a 50 ms timer processes
commands and drains events. The controller uses one background pipe writer with
a bounded queue, so its global keyboard callback never writes to a stalled pipe.
Activity packets retain their original monotonic timestamp; stale packets are rejected rather
than turning an old keyboard event into fresh authorization after a backlog.
Coalescing never crosses a configuration or flush barrier.
Bootstrap protocol 1 also returns a private 96-byte mapped activity channel.
The parent opens and validates it before publishing native readiness. Its keyboard
callback writes original PID/window/tick evidence through the x64 native POD
writer directly, without waiting for the x86 pipe or owner message pump. The
native producer's foreground/policy certificate gates those writes. Active reader
references keep mappings alive across concurrent shutdown; unmapping happens on
a background thread after all writers release their reference. The pipe remains
available for lifecycle commands and legacy activity peers.
Starting a new target drains then stops the old bridge first. `extend` keeps the
existing epoch, queue and hooks and adds newly selected targets without rebuilding.
`reconcile` stages the full automatically discovered GUI-thread set transactionally,
then retires absent subscriptions without clearing the text queue or generation.
There is no application list or fixed 32/256-thread selection cap; allocation and
hook errors are reported rather than silently truncating the set.
Stopping calls the native `TT_Flush(150)` acknowledgement barrier first, then sends
all final packets before the `stopped` response. The controller's `StopAndDrain`
waits for that ordered response and returns the tail packets before replacing the
bridge. Its stop/dispose lifecycle shares a maximum 600 ms wait budget; a timeout
is reported rather than silently treating an incomplete drain as successful.
The response also reports native threads missing the flush deadline. This is a
recoverable partial-capture warning, so an overdue thread can later deliver its
pending text through the same bridge; a healthy flush clears the warning.

Output `Type` values:

- `ready`: `Bits` is 32.
- `bootstrapped`: the native focus notification hook and shared exclusions are ready;
  `ActivityProtocol: 1` and `ActivityChannel` identify the pre-created mapped channel.
- `policyUpdated`: the native exclusion update completed.
- `started`: `Pids` and `Threads` are the selected target metadata.
- `extended`: the added target metadata was accepted without restarting the bridge.
- `reconciled`: the exact target set was accepted; `Missing` reports any retiring
  thread which missed its tail acknowledgement deadline.
- `event`: `Kind`, `ProcessId`, `ThreadId`, `Window`, `Tick`, `Text`, `Flags`, `InputSerial`
  match the existing committed-input event model.
- `diagnostics`: `Values` contains the native counter array; `Dropped` reports
  bounded queue overflow.
- `flushed`: arrived native events were written before this response; `Missing`
  reports threads which did not acknowledge the native deadline. `Id` echoes the
  request so repeated flushes cannot mistake an old response for a new one.
- `stopped`: the stop command completed.
- `error`: `Error` is an exception type and `Message` is its bounded explanation.

Native cleanup and process exit happen on stdin EOF, `exit`, or parent death.
The child never kills its parent or any target application. A target outside the
foreground or native password Edit/RichEdit control is still rejected by the
native bridge. The parent retains source/exclusion and custom protected-field
policy and owns final journaling, deduplication, and retention.

Bootstrap uses a desktop-scoped `WH_CBT` focus notification hook to install local
input observers before the first input message after focus. It does not install
a global `WH_GETMESSAGE` hook. The native producer applies shared exclusions and
protected-field checks. Policy-certified basename metadata (Kind 6, flag 512)
precedes first text, allowing the parent to attribute a short-lived producer;
metadata is consumed internally and never becomes an input-history row.
The [CBTProc contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/cbtproc)
defines `HCBT_SETFOCUS` as notification before keyboard focus changes.
The global hook belongs to a dedicated native message-pump thread which explicitly
retains the owner's desktop. The controller/companion UI can wait for lifecycle
operations without starving opposite-bitness hook callbacks. Initial TSF work is
deferred to the target's queued message after focus, avoiding COM initialization
inside the CBT focus transition itself.
