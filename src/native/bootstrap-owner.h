#pragma once
DWORD __cdecl TT_SetBootstrapPolicy(const WCHAR* exclusions,DWORD chars) {
    if(ownerPid!=GetCurrentProcessId() || !enabled)return ERROR_INVALID_STATE;
    if(!exclusions || !chars || chars>MAXDWORD/sizeof(WCHAR) || exclusions[chars-1]!=0)return ERROR_INVALID_PARAMETER;
    DWORD revision=bootstrapPolicyRevision+1;if(!revision)return ERROR_BUFFER_OVERFLOW;
    WCHAR name[128];BootstrapPolicyName(revision,name);
    HANDLE mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,0,PAGE_READWRITE,0,chars*sizeof(WCHAR),name);
    if(!mapping)return GetLastError();
    WCHAR* view=(WCHAR*)MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,(SIZE_T)chars*sizeof(WCHAR));
    if(!view) {DWORD error=GetLastError();CloseHandle(mapping);return error;}
    CopyMemory(view,exclusions,(SIZE_T)chars*sizeof(WCHAR));UnmapViewOfFile(view);
    HANDLE previous=bootstrapPolicyOwner;bootstrapPolicyOwner=mapping;
    InterlockedIncrement(&bootstrapPolicySequence);bootstrapPolicyChars=chars;bootstrapPolicyRevision=revision;InterlockedIncrement(&bootstrapPolicySequence);
    if(ownedActivityChannel)InterlockedExchange((volatile LONG*)&ownedActivityChannel->policy,(LONG)revision);
    if(previous)CloseHandle(previous);
    for(size_t i=0;i<ownedHooks.size();i++)PostThreadMessageW(ownedHooks[i].tid,WM_NULL,(WPARAM)(ULONG_PTR)MAXDWORD,(LPARAM)(ULONG_PTR)generation);
    HWND foreground=NativeForegroundWindow();DWORD pid=0;DWORD tid=GetWindowThreadProcessId(foreground,&pid);
    GUITHREADINFO gui={};gui.cbSize=sizeof(gui);
    if(tid && GetGUIThreadInfo(tid,&gui) && gui.hwndFocus)tid=GetWindowThreadProcessId(gui.hwndFocus,&pid);
    if(tid)PostThreadMessageW(tid,WM_NULL,(WPARAM)(ULONG_PTR)MAXDWORD,(LPARAM)(ULONG_PTR)generation);
    return ERROR_SUCCESS;
}
DWORD __cdecl TT_StartBootstrap(const WCHAR* exclusions,DWORD chars) {
    if(!exclusions || !chars)return ERROR_INVALID_PARAMETER;
    if(!enabled) {DWORD pid=GetCurrentProcessId(),tid=GetCurrentThreadId();DWORD error=StartSubscriptions(&pid,1,&tid,1,FALSE);if(error)return error;}
    if(ownerPid!=GetCurrentProcessId())return ERROR_ALREADY_EXISTS;
    DWORD error=TT_SetBootstrapPolicy(exclusions,chars);if(error)return error;
    error=CreateActivityChannel();if(error)return error;
    error=StartBootstrapPump();if(error)return error;
    InterlockedExchange(&ownedActivityChannel->enabled,1);InterlockedExchange(&bootstrapEnabled,1);
    for(size_t i=0;i<ownedHooks.size();i++)PostThreadMessageW(ownedHooks[i].tid,WM_NULL,(WPARAM)(ULONG_PTR)MAXDWORD,(LPARAM)(ULONG_PTR)generation);
    return ERROR_SUCCESS;
}
