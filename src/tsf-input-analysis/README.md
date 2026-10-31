# TSF changed-text observer

`TsfObserver.cpp` is an in-process, per-GUI-thread adapter for integration into the native input bridge. It does not hook an app, install a text service, create a window, activate another thread's TSF manager, or read a complete document.

## Integration

```cpp
TTsf::ThreadTsfPoll(&CopyIntoBoundedQueue, myThreadGeneration, &StillEnabled);
// on the SAME GUI thread, before teardown:
TTsf::ThreadTsfShutdown();
```

The callback ABI is `void __stdcall(HWND, const WCHAR*, DWORD length, DWORD flags, void* user)`. Flags `1` indicate changed noncomposing text, and `3` indicate a previously observed composition range whose composing property was cleared. `EnabledCallback` is `bool __stdcall(void*)`; it must check the current bridge generation as well as its enabled flag. It is checked before edit reads, per range, before `GetText`, and before delivery. Callbacks must copy immediately into a bounded, nonblocking queue; do not retain the pointer or call COM/UI from them.

Every sink has a vtable inside the bridge DLL. All observed threads must synchronously unadvise their sinks before the DLL may unload. If an owning GUI thread cannot acknowledge shutdown, the host integration must keep that DLL loaded and the generation gate false. Never call COM unadvising from `DllMain`, and do not assume removing a Windows message hook removes TSF sinks. No automatic thread-destructor COM cleanup is installed here.

The adapter obtains the existing thread manager via dynamically resolved `TF_GetThreadMgr`, advises `ITfThreadMgrEventSink`, follows the focused document's top context, and advises `ITfTextEditSink` on that context. In `OnEndEdit`, it requests text-change ranges and composing-property-change ranges separately. `ITfReadOnlyProperty::EnumRanges` splits each changed range into composing/noncomposing segments. It reads only noncomposing changed segments, or the intersection of a property-only finalization with previously observed composing ranges. Overlapping reports are deduplicated within the edit. Ranges are tracked as TSF COM ranges and translated to ACP extents at delivery.

Password input scopes and native Edit/RichEdit password styles are skipped when available, as are read-only contexts. This does not prove that a custom editor marks its sensitive fields correctly. The parent still needs application exclusions, a foreground-window check, and keyboard/edit-activity attribution: TSF can report programmatic edits as well as user edits. Whole-document ownership filtering is unnecessary for genuine narrow insertion ranges, but old document context must not be inferred from an arbitrary programmatic replacement.

## Boundaries

* Hosts must support `ITfRangeACP` and property `EnumRanges`. Enumeration failures skip a range; no fallback whole-document read occurs.
* Per edit: at most 64 update ranges, 64 property segments per update, 64 tracked composing ranges, 32 delivered nonoverlapping spans. Each span reads up to 2048 UTF-16 units. Very large paste changes can be truncated; parent integration should report/adjust its retained-character policy rather than claiming full coverage.
* Text committed before the observer was installed cannot be recovered. Property-only finalization requires that its composing range was observed earlier.
* A nonpumping/hung GUI thread cannot be made to install or clean up the observer through message hooks immediately.
* Apps that bypass TSF and IMM need a different path; WM_CHAR/IMM support belongs to the separate native bridge.

## ABI facts from installed Windows SDK 10.0.22621.0

Headers: `um/msctf.h`, `um/textstor.h`, `um/inputscope.h`.

| Interface | IID | Relevant vtable slots (IUnknown starts at 0) |
|---|---|---|
| ITfSource | `4ea48a35-60ae-446f-8fd6-e6a8d82459f7` | AdviseSink 3, UnadviseSink 4 |
| ITfThreadMgrEventSink | `aa80e80e-2021-11d2-93e0-0060b067b86e` | OnInitDocumentMgr 3, OnUninitDocumentMgr 4, OnSetFocus 5, OnPushContext 6, OnPopContext 7 |
| ITfTextEditSink | `8127d409-ccd3-4683-967a-b43d5b482bf7` | OnEndEdit 3 |
| ITfEditRecord | `42d4d099-7c1a-4a89-b836-6c6f22160df0` | GetSelectionStatus 3, GetTextAndPropertyUpdates 4 |

Use the C++ SDK interfaces and `__uuidof`, not manually reconstructed slots. `GUID_PROP_INPUTSCOPE` is defined locally from the SDK because installed `uuid.lib` does not supply this symbol. `GUID_PROP_COMPOSING` links through `uuid.lib`. Link `uuid`, `user32`, `ole32`, `oleaut32`; no `msctf.lib` is required for the dynamically resolved getter.

Official API documentation consulted:

* [GetTextAndPropertyUpdates](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfeditrecord-gettextandpropertyupdates): obtains changed text and/or requested property ranges; `TF_GTP_INCL_TEXT` with no properties obtains text changes only.
* [Predefined TSF properties](https://learn.microsoft.com/en-us/windows/win32/tsf/predefined-properties): composing property nonzero means active composition; empty means noncomposing.
* [EnumRanges](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfreadonlyproperty-enumranges): enumerates every unique property value, including empty values, clipped to the target range. It can fail when a text store does not implement attribute-transition support.
