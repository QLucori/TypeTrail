#pragma once
static HHOOK bootstrapHook;
static HANDLE bootstrapPumpThread,bootstrapPumpReady;
static DWORD bootstrapPumpTid;
static HDESK bootstrapPumpDesktop;
static volatile LONG bootstrapPumpStop,bootstrapPumpError;
static HANDLE bootstrapPolicyOwner;
static TLS HHOOK bootstrapMessageHook,bootstrapCallHook;
static TLS DWORD bootstrapEpoch,bootstrapMetadataEpoch;
static TLS HANDLE nativeOwnerHandle,bootstrapPolicyViewHandle;
static TLS DWORD nativeOwnerPid,nativeOwnerEpoch,bootstrapCachedRevision;
static TLS DWORD bootstrapCachedChars;
static TLS const WCHAR* bootstrapPolicyView;
static TLS WCHAR bootstrapProcessName[256];
static TLS DWORD bootstrapHostPid,bootstrapHostRevision;
static TLS bool bootstrapHostAllowed;
static void CloseBootstrapPolicyView();
static void BootstrapPolicyName(DWORD revision,WCHAR* name) {wsprintfW(name,L"Local\\TypeTrail.Policy.%lu.%lu.%lu",ownerPid,generation,revision);}
static bool NativeOwnerAlive() {
    if(!ownerPid)return false;
    if(nativeOwnerPid!=ownerPid || nativeOwnerEpoch!=generation) {
        CloseBootstrapPolicyView();bootstrapHostRevision=0;bootstrapMetadataEpoch=0;
        if(nativeOwnerHandle)CloseHandle(nativeOwnerHandle);
        nativeOwnerHandle=OpenProcess(SYNCHRONIZE,FALSE,ownerPid);nativeOwnerPid=ownerPid;nativeOwnerEpoch=generation;
    }
    return nativeOwnerHandle && WaitForSingleObject(nativeOwnerHandle,0)==WAIT_TIMEOUT;
}
static bool ReadProcessBaseName(DWORD pid,WCHAR* name) {
    WCHAR* path=(WCHAR*)HeapAlloc(GetProcessHeap(),0,32768*sizeof(WCHAR));if(!path)return false;
    DWORD length=32768;bool valid=false;
    if(pid==GetCurrentProcessId()) {length=GetModuleFileNameW(0,path,32768);valid=length>0 && length<32768;}
    else {HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(process){valid=QueryFullProcessImageNameW(process,0,path,&length)!=FALSE;CloseHandle(process);}}
    if(valid) {
        const WCHAR* start=wcsrchr(path,L'\\');start=start?start+1:path;size_t size=wcslen(start);
        if(size>=256)valid=false;else {CopyMemory(name,start,(size+1)*sizeof(WCHAR));if(size>4 && _wcsicmp(name+size-4,L".exe")==0)name[size-4]=0;}
    }
    SecureZeroMemory(path,32768*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,path);return valid;
}
static bool SameBitnessProcess(DWORD pid) {
    BOOL ownWow=FALSE,targetWow=FALSE;
    if(!IsWow64Process(GetCurrentProcess(),&ownWow))return false;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!process)return false;
    bool valid=IsWow64Process(process,&targetWow)!=FALSE;CloseHandle(process);
    return valid && ownWow==targetWow;
}
static void CloseBootstrapPolicyView() {
    if(bootstrapPolicyView)UnmapViewOfFile(bootstrapPolicyView);
    if(bootstrapPolicyViewHandle)CloseHandle(bootstrapPolicyViewHandle);
    bootstrapPolicyView=0;bootstrapPolicyViewHandle=0;bootstrapCachedRevision=0;
}
static bool PolicyAllowsName(const WCHAR* name) {
    const WCHAR* denied[]={L"TypeTrail",L"TypeTrail.InputHost.x86",L"LockApp",L"LogonUI",L"CredentialUIBroker",L"Consent"};
    for(size_t i=0;i<_countof(denied);i++)if(_wcsicmp(name,denied[i])==0)return false;
    LONG before=InterlockedCompareExchange(&bootstrapPolicySequence,0,0);if(before&1)return false;
    DWORD revision=bootstrapPolicyRevision,chars=bootstrapPolicyChars;
    if(before!=InterlockedCompareExchange(&bootstrapPolicySequence,0,0))return false;
    if(!revision || !chars)return false;
    if(bootstrapCachedRevision!=revision) {
        CloseBootstrapPolicyView();WCHAR mappingName[128];BootstrapPolicyName(revision,mappingName);
        bootstrapPolicyViewHandle=OpenFileMappingW(FILE_MAP_READ,FALSE,mappingName);
        if(!bootstrapPolicyViewHandle)return false;
        bootstrapPolicyView=(const WCHAR*)MapViewOfFile(bootstrapPolicyViewHandle,FILE_MAP_READ,0,0,(SIZE_T)chars*sizeof(WCHAR));
        if(!bootstrapPolicyView) {CloseBootstrapPolicyView();return false;}
        bootstrapCachedRevision=revision;
        bootstrapCachedChars=chars;
    }
    chars=bootstrapCachedChars;
    const WCHAR* policy=bootstrapPolicyView;size_t nameLength=wcslen(name);
    for(DWORD begin=0;begin<chars;) {
        while(begin<chars && (policy[begin]==0 || policy[begin]==L',' || policy[begin]==L';' || policy[begin]==0xFF0C || policy[begin]<=L' '))++begin;
        DWORD end=begin;while(end<chars && policy[end] && policy[end]!=L',' && policy[end]!=L';' && policy[end]!=0xFF0C && policy[end]!=L'\r' && policy[end]!=L'\n')++end;
        DWORD trimmed=end;while(trimmed>begin && policy[trimmed-1]<=L' ')--trimmed;
        if(trimmed-begin>4 && _wcsnicmp(policy+trimmed-4,L".exe",4)==0)trimmed-=4;
        if(trimmed-begin==nameLength && _wcsnicmp(policy+begin,name,nameLength)==0)return false;
        begin=end<chars?end+1:end;
    }
    return revision==bootstrapPolicyRevision;
}
static bool NativeProtectedWindow(HWND window) {
    WCHAR cls[128]={};GetClassNameW(window,cls,128);CharUpperBuffW(cls,(DWORD)wcslen(cls));
    bool native=wcscmp(cls,L"EDIT")==0 || wcsncmp(cls,L"RICHEDIT",8)==0 || (wcsncmp(cls,L"WINDOWSFORMS",12)==0 && (wcsstr(cls,L".EDIT.") || wcsstr(cls,L".RICHEDIT")));
    return native && (GetWindowLongPtrW(window,GWL_STYLE)&(ES_PASSWORD|ES_READONLY));
}
static bool KnownNativeEditable(HWND window) {
    WCHAR cls[128]={};GetClassNameW(window,cls,128);CharUpperBuffW(cls,(DWORD)wcslen(cls));
    bool native=wcscmp(cls,L"EDIT")==0 || wcsncmp(cls,L"RICHEDIT",8)==0 || (wcsncmp(cls,L"WINDOWSFORMS",12)==0 && (wcsstr(cls,L".EDIT.") || wcsstr(cls,L".RICHEDIT")));
    return native && !(GetWindowLongPtrW(window,GWL_STYLE)&(ES_PASSWORD|ES_READONLY));
}
static void RefreshBootstrapHostPolicy(HWND target) {
    HWND root=GetAncestor(target,GA_ROOT);DWORD pid=0;GetWindowThreadProcessId(root,&pid);
    if(!pid)return;
    if(bootstrapHostPid==pid && bootstrapHostRevision==bootstrapPolicyRevision)return;
    WCHAR name[256]={};bootstrapHostAllowed=ReadProcessBaseName(pid,name) && PolicyAllowsName(name);
    bootstrapHostPid=pid;bootstrapHostRevision=bootstrapPolicyRevision;
}
static bool BootstrapProcessAllowed() {
    if(!enabled || !bootstrapEnabled || !NativeOwnerAlive() || GetCurrentProcessId()==ownerPid)return false;
    if(!bootstrapProcessName[0] && !ReadProcessBaseName(GetCurrentProcessId(),bootstrapProcessName))return false;
    DWORD hostPid=0;GetWindowThreadProcessId(NativeForegroundWindow(),&hostPid);
    return PolicyAllowsName(bootstrapProcessName) && bootstrapHostPid==hostPid && bootstrapHostRevision==bootstrapPolicyRevision && bootstrapHostAllowed;
}
static bool BootstrapSubscribed() {return bootstrapEpoch==generation && BootstrapProcessAllowed();}
static bool BootstrapCertified() {return bootstrapMetadataEpoch==generation && bootstrapEnabled && NativeOwnerAlive();}
static bool BootstrapAllowed(HWND window) {
    DWORD pid=0;DWORD tid=GetWindowThreadProcessId(window,&pid);
    return BootstrapSubscribed() && tid==GetCurrentThreadId() && pid==GetCurrentProcessId() && !NativeProtectedWindow(window) && GetAncestor(window,GA_ROOT)==GetAncestor(NativeForegroundWindow(),GA_ROOT);
}
static void UnhookLocalBootstrap() {
    if(bootstrapMessageHook){UnhookWindowsHookEx(bootstrapMessageHook);bootstrapMessageHook=0;}
    if(bootstrapCallHook){UnhookWindowsHookEx(bootstrapCallHook);bootstrapCallHook=0;}
    bootstrapEpoch=0;
}
static bool AnnounceBootstrap(HWND target) {
    if(bootstrapMetadataEpoch==generation)return true;
    pendingGeneration=generation;
    if(!Push(target,6,bootstrapProcessName,(DWORD)wcslen(bootstrapProcessName),512,true,inputSerial,GetTickCount64()))return false;
    bootstrapMetadataEpoch=generation;return true;
}
static bool PublishBootstrapForeground(HWND target) {
    ForegroundCertificate* certificate=CurrentForegroundCertificate();
    LONG before=InterlockedCompareExchange(&certificate->sequence,0,0);
    if((before&1) || InterlockedCompareExchange(&certificate->sequence,before+1,before)!=before)return false;
    certificate->pid=GetCurrentProcessId();certificate->epoch=generation;certificate->policy=bootstrapPolicyRevision;
    certificate->root=(ULONGLONG)(ULONG_PTR)GetAncestor(target,GA_ROOT);certificate->focus=(ULONGLONG)(ULONG_PTR)target;
    InterlockedExchange(&certificate->sequence,before+2);return true;
}
static LRESULT CALLBACK BootstrapMessages(int code,WPARAM w,LPARAM l) {
    if(!NativeOwnerAlive() || !enabled || bootstrapEpoch!=generation) {pendingCharacter=FALSE;UnhookLocalBootstrap();return CallNextHookEx(0,code,w,l);}
    if(CurrentRegistration()) {
        bool forwarded=!ownerMessageDispatchDepth && IsStagedHookPass(code,w,l,false);
        // A nested GetMessage may reach this hook while an outer staged
        // delivery still needs it. Retire only after that delivery finishes.
        if(!forwarded){if(!stagedHookPass)UnhookLocalBootstrap();return CallNextHookEx(0,code,w,l);}
        LRESULT result=MessageHookCore(code,w,l,true);UnhookLocalBootstrap();return result;
    }
    if(code==HC_ACTION && w==PM_REMOVE && bootstrapMetadataEpoch!=generation) {const MSG* message=(const MSG*)l;HWND target=InputTarget(message->hwnd,message->message);if(!AnnounceBootstrap(target))return CallNextHookEx(0,code,w,l);}
    return MessageHookCore(code,w,l,true);
}
static LRESULT CALLBACK BootstrapCalls(int code,WPARAM w,LPARAM l) {
    if(!NativeOwnerAlive() || !enabled || bootstrapEpoch!=generation) {pendingCharacter=FALSE;UnhookLocalBootstrap();return CallNextHookEx(0,code,w,l);}
    if(CurrentRegistration()) {
        bool forwarded=!ownerCallDispatchDepth && IsStagedHookPass(code,w,l,true);
        if(!forwarded){if(!stagedHookPass)UnhookLocalBootstrap();return CallNextHookEx(0,code,w,l);}
        LRESULT result=CallHookCore(code,w,l,true);UnhookLocalBootstrap();return result;
    }
    return CallHookCore(code,w,l,true);
}
static LRESULT CALLBACK BootstrapCbt(int code,WPARAM w,LPARAM l) {
    if(code==HCBT_SETFOCUS && enabled && bootstrapEnabled && GetCurrentProcessId()!=ownerPid && NativeOwnerAlive()) {
        HWND target=(HWND)w;DWORD pid=0;DWORD tid=GetWindowThreadProcessId(target,&pid);
        if(pid==GetCurrentProcessId() && tid==GetCurrentThreadId() && GetAncestor(target,GA_ROOT)==GetAncestor(NativeForegroundWindow(),GA_ROOT)) {
            RefreshBootstrapHostPolicy(target);
            if(BootstrapProcessAllowed() && !NativeProtectedWindow(target)) {
                bootstrapEpoch=generation;
                if(!CurrentRegistration() && !bootstrapMessageHook) {
                    bootstrapMessageHook=SetWindowsHookExW(WH_GETMESSAGE,BootstrapMessages,module,GetCurrentThreadId());
                    bootstrapCallHook=SetWindowsHookExW(WH_CALLWNDPROC,BootstrapCalls,module,GetCurrentThreadId());
                    if(!bootstrapMessageHook || !bootstrapCallHook)UnhookLocalBootstrap();
                }
                if(bootstrapEpoch==generation) {
                    AnnounceBootstrap(target);PublishBootstrapForeground(target);
                    // HCBT_SETFOCUS runs before the editor's focus transition
                    // completes. Initialize TSF after returning from it; the
                    // installed GetMessage hook also polls before key dispatch.
                    PostThreadMessageW(GetCurrentThreadId(),WM_NULL,0,0);
                }
            }
        }
    }
    return CallNextHookEx(0,code,w,l);
}
static DWORD WINAPI RunBootstrapPump(void*) {
    // Global hooks of the opposite bitness are called on their installer.
    // Keep that installer independent of controller UI/pipe work. Explicit
    // desktop propagation is essential for isolated-desktop tests too.
    DWORD error=ERROR_SUCCESS;
    if(!SetThreadDesktop(bootstrapPumpDesktop))error=GetLastError();
    MSG message={};
    if(!error)PeekMessageW(&message,0,0,0,PM_NOREMOVE);
    if(!error && !InterlockedCompareExchange(&bootstrapPumpStop,0,0)) {
        HHOOK hook=SetWindowsHookExW(WH_CBT,BootstrapCbt,module,0);
        if(!hook)error=GetLastError();
        else InterlockedExchangePointer((PVOID volatile*)&bootstrapHook,hook);
    }
    InterlockedExchange(&bootstrapPumpError,(LONG)error);SetEvent(bootstrapPumpReady);
    if(!error && bootstrapHook && !InterlockedCompareExchange(&bootstrapPumpStop,0,0)) {
        while(GetMessageW(&message,0,0,0)>0 && !InterlockedCompareExchange(&bootstrapPumpStop,0,0)) {
            TranslateMessage(&message);DispatchMessageW(&message);
        }
    }
    HHOOK hook=(HHOOK)InterlockedExchangePointer((PVOID volatile*)&bootstrapHook,0);
    if(hook)UnhookWindowsHookEx(hook);
    return error;
}
static void CloseBootstrapPumpHandles() {
    if(bootstrapPumpThread)CloseHandle(bootstrapPumpThread);
    if(bootstrapPumpReady)CloseHandle(bootstrapPumpReady);
    bootstrapPumpThread=bootstrapPumpReady=0;bootstrapPumpTid=0;
}
static DWORD StartBootstrapPump() {
    if(bootstrapPumpThread) {
        if(WaitForSingleObject(bootstrapPumpThread,0)==WAIT_TIMEOUT)return bootstrapHook?ERROR_SUCCESS:ERROR_BUSY;
        CloseBootstrapPumpHandles();
    }
    bootstrapPumpDesktop=GetThreadDesktop(GetCurrentThreadId());
    bootstrapPumpReady=CreateEventW(0,TRUE,FALSE,0);if(!bootstrapPumpReady)return GetLastError();
    InterlockedExchange(&bootstrapPumpStop,0);InterlockedExchange(&bootstrapPumpError,0);
    bootstrapPumpThread=CreateThread(0,0,RunBootstrapPump,0,0,&bootstrapPumpTid);
    if(!bootstrapPumpThread) {DWORD error=GetLastError();CloseBootstrapPumpHandles();return error;}
    DWORD wait=WaitForSingleObject(bootstrapPumpReady,5000);
    if(wait!=WAIT_OBJECT_0) {
        InterlockedExchange(&bootstrapPumpStop,1);PostThreadMessageW(bootstrapPumpTid,WM_QUIT,0,0);
        return wait==WAIT_TIMEOUT?ERROR_TIMEOUT:GetLastError();
    }
    DWORD error=(DWORD)InterlockedCompareExchange(&bootstrapPumpError,0,0);
    if(!error && !bootstrapHook)error=ERROR_OPERATION_ABORTED;
    return error;
}
static void StopBootstrap() {
    InterlockedExchange(&bootstrapEnabled,0);
    if(ownedActivityChannel)InterlockedExchange(&ownedActivityChannel->enabled,0);
    InterlockedExchange(&bootstrapPumpStop,1);
    HHOOK hook=(HHOOK)InterlockedExchangePointer((PVOID volatile*)&bootstrapHook,0);
    if(hook)UnhookWindowsHookEx(hook);
    if(bootstrapPumpTid)PostThreadMessageW(bootstrapPumpTid,WM_QUIT,0,0);
    if(bootstrapPumpThread && WaitForSingleObject(bootstrapPumpThread,1000)==WAIT_OBJECT_0)CloseBootstrapPumpHandles();
    if(bootstrapPolicyOwner){CloseHandle(bootstrapPolicyOwner);bootstrapPolicyOwner=0;}
    CloseBootstrapPolicyView();
}
