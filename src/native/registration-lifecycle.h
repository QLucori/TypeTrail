#pragma once
static DWORD lastRetireMissing;
static bool ContainsId(const std::vector<DWORD>& values,DWORD value) { return std::find(values.begin(),values.end(),value)!=values.end(); }
static void UnhookOwned(const OwnedHook& hook) {
    RegistrationSlot* slot=RegistrationAt(hook.slot);
    if(slot) {InterlockedExchange((volatile LONG*)&slot->activeTid,0);InterlockedExchange64((volatile LONGLONG*)&slot->wakeWindow,0);}
    if(hook.message)UnhookWindowsHookEx(hook.message);
    if(hook.call)UnhookWindowsHookEx(hook.call);
}
static DWORD UpdateSubscriptions(const DWORD* pids,DWORD pidCount,const DWORD* tids,DWORD tidCount,bool replace) {
    if(!pids || !pidCount || !tids || !tidCount)return ERROR_INVALID_PARAMETER;
    std::vector<DWORD> requestedPids,requestedTids,addedPids;
    std::vector<OwnedHook> additions;
    try {
        requestedPids.reserve(pidCount);requestedTids.reserve(tidCount);additions.reserve(tidCount);
        for(DWORD i=0;i<pidCount;i++) {if(!pids[i])return ERROR_INVALID_PARAMETER;if(!ContainsId(requestedPids,pids[i]))requestedPids.push_back(pids[i]);}
        for(DWORD i=0;i<tidCount;i++) {if(!tids[i])return ERROR_INVALID_PARAMETER;if(!ContainsId(requestedTids,tids[i]))requestedTids.push_back(tids[i]);}
        for(size_t i=0;i<requestedPids.size();i++)if(!TargetAllowed(requestedPids[i]))addedPids.push_back(requestedPids[i]);
        ownedHooks.reserve(ownedHooks.size()+requestedTids.size());
    }catch(const std::bad_alloc&) { return ERROR_NOT_ENOUGH_MEMORY; }
    if(addedPids.size()>MAXDWORD-targetCount || requestedTids.size()>MAXDWORD-registrationSlotCount)return ERROR_BUFFER_OVERFLOW;
    DWORD error=EnsureRegistrationCapacity((std::max)(targetCount+(DWORD)addedPids.size(),registrationSlotCount+(DWORD)requestedTids.size()));
    if(error)return error;
    for(size_t i=0;i<requestedTids.size();i++) {
        DWORD tid=requestedTids[i];HANDLE thread=OpenThread(THREAD_QUERY_LIMITED_INFORMATION,FALSE,tid);
        if(!thread) { error=GetLastError();break; }
        DWORD pid=GetProcessIdOfThread(thread);ULONGLONG birth=ThreadBirth(thread);CloseHandle(thread);
        if(!birth || !ContainsId(requestedPids,pid)) { error=ERROR_INVALID_PARAMETER;break; }
        if(!SameBitnessProcess(pid)) {error=ERROR_BAD_EXE_FORMAT;break;}
        bool exists=false;
        for(size_t j=0;j<ownedHooks.size();j++)if(ownedHooks[j].tid==tid && ownedHooks[j].pid==pid && ownedHooks[j].birth==birth) {exists=true;break;}
        if(exists)continue;
        OwnedHook hook={tid,pid,registrationSlotCount++,birth,0,0};
        hook.message=SetWindowsHookExW(WH_GETMESSAGE,MessageHook,module,tid);
        if(!hook.message) { error=GetLastError();break; }
        hook.call=SetWindowsHookExW(WH_CALLWNDPROC,CallHook,module,tid);
        if(!hook.call) {error=GetLastError();UnhookWindowsHookEx(hook.message);break;}
        additions.push_back(hook);
    }
    if(error) {for(size_t i=0;i<additions.size();i++)UnhookOwned(additions[i]);return error;}
    // Existing accepted tails are flushed before retiring their hooks. New
    // hooks are staged but cannot admit callbacks until their slot is active.
    if(replace && enabled)lastRetireMissing=TT_Flush(150);
    if(InterlockedCompareExchange(&gate,1,0)!=0) {
        for(size_t i=0;i<additions.size();i++)UnhookOwned(additions[i]);return ERROR_BUSY;
    }
    if(replace)for(DWORD i=0;i<targetCount;i++) {
        RegistrationSlot* slot=RegistrationAt(i);
        if(slot && slot->targetPid && !ContainsId(requestedPids,slot->targetPid))slot->targetPid=0;
    }
    for(size_t i=0;i<addedPids.size();i++)RegistrationAt(targetCount++)->targetPid=addedPids[i];
    for(size_t i=0;i<additions.size();i++) {
        const OwnedHook& hook=additions[i];RegistrationSlot* slot=RegistrationAt(hook.slot);
        slot->activePid=hook.pid;slot->threadBirth=hook.birth;slot->epoch=generation;
        MemoryBarrier();InterlockedExchange((volatile LONG*)&slot->activeTid,(LONG)hook.tid);
    }
    MemoryBarrier();InterlockedExchange(&gate,0);
    if(replace)for(size_t i=0;i<ownedHooks.size();) {
        const OwnedHook& hook=ownedHooks[i];bool keep=ContainsId(requestedTids,hook.tid) && ContainsId(requestedPids,hook.pid);
        // A recycled TID/PID with a different creation identity gets its new
        // physical slot; a late old callback can never address that new slot.
        for(size_t j=0;keep && j<additions.size();j++)if(additions[j].tid==hook.tid)keep=false;
        if(!keep) {UnhookOwned(hook);ownedHooks.erase(ownedHooks.begin()+i);} else ++i;
    }
    ownedHooks.insert(ownedHooks.end(),additions.begin(),additions.end());
    if(bootstrapEnabled)for(size_t i=0;i<additions.size();i++)PostThreadMessageW(additions[i].tid,WM_NULL,(WPARAM)(ULONG_PTR)MAXDWORD,(LPARAM)(ULONG_PTR)generation);
    return ERROR_SUCCESS;
}
static DWORD StartSubscriptions(const DWORD* pids,DWORD count,const DWORD* tids,DWORD threads,BOOL testOwner) {
    if(!pids || !count || !tids || !threads)return ERROR_INVALID_PARAMETER;
    if(InterlockedCompareExchange(&gate,1,0)!=0)return ERROR_BUSY;
    if(ownerPid && ownerPid!=GetCurrentProcessId()) {
        HANDLE previous=OpenProcess(SYNCHRONIZE,FALSE,ownerPid);bool dead=previous && WaitForSingleObject(previous,0)==WAIT_OBJECT_0;
        if(!previous && GetLastError()==ERROR_INVALID_PARAMETER)dead=true;if(previous)CloseHandle(previous);
        if(!dead) {InterlockedExchange(&gate,0);return ERROR_ALREADY_EXISTS;}
        enabled=0;
    }
    if(enabled) {InterlockedExchange(&gate,0);return ERROR_ALREADY_EXISTS;}
    if(InterlockedCompareExchange(&queueWriterRefs,0,0)!=0) {InterlockedExchange(&gate,0);return ERROR_BUSY;}
    CloseRegistrationViews();CloseOwnedActivityChannel();ownedHooks.clear();
    ownerPid=GetCurrentProcessId();registrationOwnerThread=GetCurrentThreadId();includeOwner=testOwner;++generation;if(!generation)++generation;
    targetCount=registrationPageCount=registrationSlotCount=0;readPosition=writePosition=0;dropped=0;flushSequence=0;ZeroMemory((void*)stats,sizeof(stats));
    for(DWORD i=0;i<TT_CAPACITY;i++)queueSequences[i]=(LONG)i;
    InterlockedExchange(&gate,0);
    DWORD error=UpdateSubscriptions(pids,count,tids,threads,false);
    if(error) { TT_Stop();return error; }
    InterlockedExchange(&enabled,1);return ERROR_SUCCESS;
}
DWORD __cdecl TT_Start(DWORD tid,const DWORD* pids,DWORD count,BOOL testOwner) { return tid?StartSubscriptions(pids,count,&tid,1,testOwner):ERROR_INVALID_PARAMETER; }
DWORD __cdecl TT_StartThreads(const DWORD* pids,DWORD count,const DWORD* tids,DWORD threads,BOOL testOwner) { return StartSubscriptions(pids,count,tids,threads,testOwner); }
DWORD __cdecl TT_Extend(const DWORD* pids,DWORD count,const DWORD* tids,DWORD threads) {
    return ownerPid==GetCurrentProcessId() && enabled?UpdateSubscriptions(pids,count,tids,threads,false):ERROR_INVALID_STATE;
}
DWORD __cdecl TT_Reconcile(const DWORD* pids,DWORD count,const DWORD* tids,DWORD threads) {
    return ownerPid==GetCurrentProcessId() && enabled?UpdateSubscriptions(pids,count,tids,threads,true):ERROR_INVALID_STATE;
}
DWORD __cdecl TT_LastRetireMissing() { return ownerPid==GetCurrentProcessId()?lastRetireMissing:0; }
DWORD __cdecl TT_Flush(DWORD timeout) {
    if(ownerPid!=GetCurrentProcessId() || !enabled)return 0;
    LONG sequence=InterlockedIncrement(&flushSequence);if(!sequence)sequence=InterlockedIncrement(&flushSequence);
    for(size_t i=0;i<ownedHooks.size();i++) {
        RegistrationSlot* slot=RegistrationAt(ownedHooks[i].slot);if(!slot)continue;
        if(ownedHooks[i].tid==GetCurrentThreadId()) {FlushCharacter();InterlockedExchange(&slot->flushAcknowledged,sequence);}
        else {
            if(!PostThreadMessageW(ownedHooks[i].tid,WM_NULL,(WPARAM)(ULONG_PTR)(DWORD)sequence,(LPARAM)(ULONG_PTR)generation) && GetLastError()==ERROR_INVALID_THREAD_ID)InterlockedExchange(&slot->flushAcknowledged,sequence);
            auto wake=[&](HWND target) {
                DWORD pid=0;DWORD tid=target?GetWindowThreadProcessId(target,&pid):0;
                if(tid==ownedHooks[i].tid && pid==ownedHooks[i].pid && slot->epoch==generation)PostMessageW(target,WM_NULL,(WPARAM)(ULONG_PTR)(DWORD)sequence,(LPARAM)(ULONG_PTR)generation);
            };
            HWND target=(HWND)(ULONG_PTR)InterlockedCompareExchange64((volatile LONGLONG*)&slot->wakeWindow,0,0);wake(target);
            GUITHREADINFO gui={};gui.cbSize=sizeof(gui);
            if(GetGUIThreadInfo(ownedHooks[i].tid,&gui) && gui.hwndFocus!=target)wake(gui.hwndFocus);
        }
    }
    ULONGLONG until=GetTickCount64()+(timeout>500?500:timeout);DWORD missing=0;
    do {
        missing=0;for(size_t i=0;i<ownedHooks.size();i++) {RegistrationSlot* slot=RegistrationAt(ownedHooks[i].slot);if(slot && InterlockedCompareExchange(&slot->flushAcknowledged,0,0)!=sequence)++missing;}
        if(!missing || GetTickCount64()>=until)break;Sleep(1);
    }while(true);
    return missing;
}
void __cdecl TT_Stop() {
    if(ownerPid!=GetCurrentProcessId())return;
    InterlockedExchange(&enabled,0);
    StopBootstrap();
#ifdef TT_WITH_TSF
    for(size_t i=0;i<ownedHooks.size();i++)PostThreadMessageW(ownedHooks[i].tid,WM_NULL,0,0);
    ShutdownThread();
    ULONGLONG until=GetTickCount64()+250;
    do {
        bool waiting=false;for(size_t i=0;i<ownedHooks.size();i++) {RegistrationSlot* slot=RegistrationAt(ownedHooks[i].slot);if(slot && slot->watched.live)waiting=true;}
        if(!waiting || GetTickCount64()>=until)break;Sleep(1);
    }while(true);
#endif
    pendingCharacter=FALSE;
    for(size_t i=0;i<ownedHooks.size();i++)UnhookOwned(ownedHooks[i]);ownedHooks.clear();
    ULONGLONG writerDeadline=GetTickCount64()+250;
    while(InterlockedCompareExchange(&queueWriterRefs,0,0)!=0 && GetTickCount64()<writerDeadline)Sleep(1);
    // A suspended writer owns its payload until completion. Leave ownership
    // reserved and refuse restart instead of resetting memory beneath it.
    if(InterlockedCompareExchange(&queueWriterRefs,0,0)!=0)return;
    if(InterlockedCompareExchange(&gate,1,0)==0) {
        SecureZeroMemory(queue,sizeof(queue));targetCount=0;readPosition=writePosition=0;InterlockedExchange(&gate,0);
    }
    // Foreign threads' own mapped views keep retired pages valid until their
    // callbacks finish or their process exits, independently of owner handles.
    CloseRegistrationViews();
    CloseOwnedActivityChannel();
    ownerPid=0;
}
BOOL __cdecl TT_NotifyInputAt(DWORD pid,ULONGLONG window,ULONGLONG tick) {
    ULONGLONG now=GetTickCount64();if(!tick || tick>now || now-tick>=5000)return FALSE;
    if(!enabled || ownerPid!=GetCurrentProcessId() || !pid || !window)return FALSE;
    HWND hwnd=(HWND)(ULONG_PTR)window,foreground=NativeForegroundWindow();DWORD actual=0;GetWindowThreadProcessId(hwnd,&actual);
    if(actual!=pid || GetAncestor(hwnd,GA_ROOT)!=GetAncestor(foreground,GA_ROOT))return FALSE;
    struct Reference {Reference(){InterlockedIncrement(&queueWriterRefs);}~Reference(){InterlockedDecrement(&queueWriterRefs);}} reference;
    if(!enabled || ownerPid!=GetCurrentProcessId())return FALSE;
    InputActivity* selected=0;
    if(bootstrapEnabled) {
        ForegroundCertificate* certificate=CurrentForegroundCertificate();
        LONG before=InterlockedCompareExchange(&certificate->sequence,0,0);
        DWORD certifiedPid=certificate->pid,epoch=certificate->epoch,policy=certificate->policy;
        ULONGLONG root=certificate->root,focus=certificate->focus;
        LONG after=InterlockedCompareExchange(&certificate->sequence,0,0);
        if((before&1) || before!=after || certifiedPid!=pid || epoch!=generation || policy!=bootstrapPolicyRevision || focus!=window || root!=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT))return FALSE;
        selected=CurrentBootstrapKeyboard();
    }
    if(selected) {
        LONG before=InterlockedCompareExchange(&selected->sequence,0,0);
        if((before&1) || InterlockedCompareExchange(&selected->sequence,before+1,before)!=before)return FALSE;
        ULONGLONG root=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT);
        if(selected->pid==pid && selected->epoch==generation && selected->root==root && selected->tick>tick) {InterlockedExchange(&selected->sequence,before);return TRUE;}
        selected->pid=pid;selected->epoch=generation;selected->root=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT);selected->tick=tick;
        InterlockedExchange(&selected->sequence,before+2);InterlockedIncrement(&stats[10]);return TRUE;
    }
    for(DWORD i=0;i<targetCount;i++) {
        RegistrationSlot* slot=RegistrationAt(i);if(!slot || slot->targetPid!=pid)continue;
        InputActivity& input=slot->activity;
        LONG before=InterlockedCompareExchange(&input.sequence,0,0);
        if((before&1) || InterlockedCompareExchange(&input.sequence,before+1,before)!=before)return FALSE;
        ULONGLONG root=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT);
        if(input.pid==pid && input.epoch==generation && input.root==root && input.tick>tick) {InterlockedExchange(&input.sequence,before);return TRUE;}
        input.pid=pid;input.epoch=generation;input.root=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT);input.tick=tick;
        InterlockedExchange(&input.sequence,before+2);InterlockedIncrement(&stats[10]);return TRUE;
    }
    return FALSE;
}
BOOL __cdecl TT_NotifyInput(DWORD pid,ULONGLONG window) { return TT_NotifyInputAt(pid,window,GetTickCount64()); }
