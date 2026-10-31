#pragma once
#include <windows.h>

namespace TTsf {
// Called synchronously inside OnEndEdit on the editor's owning GUI thread.
// The receiver MUST copy into bounded storage and MUST NOT call COM/UI/IPC waits.
typedef void (__stdcall* CommitCallback)(HWND hwnd, const WCHAR* text, DWORD length, DWORD flags, void* user);
typedef bool (__stdcall* EnabledCallback)(void* user);
// Flags 1 = changed noncomposing text, 3 = observed composition finalized.
// This observes the existing TSF thread manager; it never activates a text service.
bool ThreadTsfPoll(CommitCallback callback, void* user, EnabledCallback enabled = nullptr);
// Same-thread cached composition metadata; does not read document text.
// Poll refreshes the cache from current CompositionView metadata, so retired
// tracked ranges cannot permanently suppress ordinary character input.
bool ThreadTsfHasComposition();
// Metadata only, per-thread counters (cumulative until thread exit), except index3
// which is the currently-bound-context flag. Returns copied count, or 24 if values
// is null. Order: polls, managerGetterSuccesses, focusedDocumentObservations,
// contextBound, contextAdviseSuccesses, endEditEntriesBeforeGate,
// compositionDeliveries, ordinaryDeliveries.
// Indices 8..23: acceptedEndEdit, disabledGate, reentrantEdit, wrongContext,
// protectedRange, updateEnumerationFailures, propertyUpdateRanges, textUpdateRanges,
// GetTextFailures, emptyGetText, successfulGetTextChunks, unknownCompositionState,
// lastContextStatusHRESULT, lastContextDynamicFlags, lastGetTextHRESULT,
// croppedOrIncompleteRangeReads. The first eight counters remain ABI-compatible.
DWORD ThreadTsfDiagnostics(DWORD* values, DWORD capacity);
// Owning-thread metadata COM query only. Returns zero if active-view HWND is
// unavailable; callers must not infer a target window from that absence.
HWND ThreadTsfContextWindow();
// MUST run on the same GUI thread before unloading the containing DLL.
void ThreadTsfShutdown();
}
