#pragma once
#include <windows.h>
#define TT_TEXT 1
#define TT_BACKSPACE 2
#define TT_MAX_TEXT 256
#define TT_CAPACITY 2048
#pragma pack(push, 8)
typedef struct TT_EVENT { DWORD kind, pid, tid, length; ULONGLONG window, tick; WCHAR text[TT_MAX_TEXT]; DWORD flags, inputSerial; } TT_EVENT;
#pragma pack(pop)
extern "C" {
__declspec(dllexport) DWORD __cdecl TT_Start(DWORD threadId, const DWORD* targets, DWORD count, BOOL includeOwnerForTest);
__declspec(dllexport) DWORD __cdecl TT_StartThreads(const DWORD* targets, DWORD count, const DWORD* threadIds, DWORD threadCount, BOOL includeOwnerForTest);
__declspec(dllexport) DWORD __cdecl TT_Extend(const DWORD* targets, DWORD count, const DWORD* threadIds, DWORD threadCount);
__declspec(dllexport) DWORD __cdecl TT_Reconcile(const DWORD* targets, DWORD count, const DWORD* threadIds, DWORD threadCount);
__declspec(dllexport) DWORD __cdecl TT_LastRetireMissing();
__declspec(dllexport) DWORD __cdecl TT_Flush(DWORD timeoutMilliseconds);
__declspec(dllexport) void __cdecl TT_Stop();
__declspec(dllexport) DWORD __cdecl TT_Read(TT_EVENT* events, DWORD capacity);
__declspec(dllexport) DWORD __cdecl TT_Dropped();
__declspec(dllexport) DWORD __cdecl TT_Stats(DWORD* values,DWORD capacity);
__declspec(dllexport) BOOL __cdecl TT_NotifyInput(DWORD pid,ULONGLONG window);
__declspec(dllexport) BOOL __cdecl TT_NotifyInputAt(DWORD pid,ULONGLONG window,ULONGLONG originalTick);
__declspec(dllexport) DWORD __cdecl TT_StartBootstrap(const WCHAR* exclusions,DWORD chars);
__declspec(dllexport) DWORD __cdecl TT_SetBootstrapPolicy(const WCHAR* exclusions,DWORD chars);
__declspec(dllexport) DWORD __cdecl TT_GetActivityChannelName(WCHAR* output,DWORD capacity);
__declspec(dllexport) BOOL __cdecl TT_WriteActivity(void* mapped,DWORD pid,ULONGLONG window,ULONGLONG originalTick);
}
