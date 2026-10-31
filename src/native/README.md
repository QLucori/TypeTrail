# Windows committed input bridge

The product automatically follows the foreground application and selects its
GUI/input threads. There is no application-name allowlist and users do not add
applications. Source labels are cosmetic. The caller selects bounded native PIDs
and threads from actual foreground window ownership, including hidden TSF GUI
threads; callbacks revalidate that foreground ownership before text reads.

The x64 controller and the x86 companion use matching-bitness native DLLs.
`NativeCapture.cs` selects the matching transport automatically. The x86 helper
exists only while needed; it communicates with its parent through private pipes,
exits on EOF/parent death, and installs/uninstalls hooks on its own STA thread.
See `../companion/README.md` for its protocol and isolated integration tests.

The bridge receives removed Unicode WM_CHAR messages, IMM finalized result
strings, and TSF changed/finalized composition ranges. It never reads a complete
document, webpage, chat database, screenshot, or network response. Ordinary TSF
range notifications require a match against actual typed-character evidence;
composition results are journaled as committed text, with preedit excluded.
The parent also accepts UIA composition-finalization events generically. A real
Ctrl+V can be confirmed against the focused field when WM_PASTE produces no
character event; only that command's pasted text enters the journal.

IMM may forward the same finalized result through several windows. Version 1.7
coalesces equal IMM notifications only within one real keyboard-activity serial.
A later input serial preserves a genuinely repeated word. Pending WM_CHAR/IMM/
TSF aliases are coalesced. New targets are selected on focus changes, and queued
commits are drained before the old subscription is replaced.

The image-shared queue is bounded at 512 packets of 256 UTF-16 units. Producers
drop and report overflow rather than block the target. Native ES_PASSWORD and
TSF protected input scopes are rejected, with UIA sensitive-field policy and
user exclusions retained in the parent. Pause/exit disables capture before
removing hooks. Versioned DLL names allow old pinned, disabled modules to coexist
until the host application exits, without forcing editors or chats to close.

This is a user-session, user-mode input observer, not a kernel input driver.
Different sessions, protected processes, secure desktops and higher-integrity
targets remain subject to Windows access rules. An app that bypasses WM_CHAR,
IMM, TSF and UIA cannot be claimed covered without runtime evidence. WM_CHAR
observes character delivery, not a guarantee that every custom editor accepted
the character. Navigation/selection cannot reconstruct an existing document;
the journal deliberately retains only entered characters.

Build x64 with `build.ps1`; build x86 with `../companion/build.ps1`.
The matching TSF observer is in `../tsf-input-analysis`.
Official contracts: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw

1.7.1 retains existing hooks while extending the bounded foreground target set.
TT_Extend preserves the generation and unread queue. TT_Flush posts a same-thread
wake and waits up to the requested bound (500ms maximum) for deferred characters
to enter the queue. A WM_CHAR already authorized in its original foreground
editor retains that authorization and original timestamp during a later flush.
Only metadata/protection and IME-finalization events are polled normally; full
field reads are reserved for confirming an actual paste command. The old document
snapshot journal is not used by the running controller. UIA finalizations retain
their original PID/thread/window/timestamp across unsubscription and focus changes.
Stop/pause/exit/panel display drain their pending committed events first.
