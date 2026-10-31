#pragma once
#include <vector>
#include <algorithm>
#include <new>
#include <stddef.h>
// Pages are append-only within a generation. Retired physical slots are never
// reused: an in-flight callback/ack can only refer to its original registration.
static const DWORD RegistrationPageEntries=64;
struct RegistrationSlot {
    DWORD targetPid,activeTid,activePid,epoch;
    ULONGLONG threadBirth;
    WatchedThread watched;
    InputActivity activity;
    volatile LONG flushAcknowledged;
    // Keep the last input target after its buffer was flushed too: a controller
    // barrier must still reach HWND-filtered loops. Never store field text.
    ULONGLONG wakeWindow;
};
struct RegistrationPage { RegistrationSlot entries[RegistrationPageEntries]; };
static_assert(offsetof(RegistrationSlot,wakeWindow)%8==0,"Deferred HWND must be Interlocked64-aligned");
struct RegistrationView { DWORD index;HANDLE mapping;RegistrationPage* page;RegistrationView* next; };
static TLS RegistrationView* registrationViews;
static TLS DWORD registrationViewOwner,registrationViewEpoch;
static RegistrationView* ownerRegistrationViews;
static DWORD registrationOwnerThread;
static TLS DWORD cachedRegistrationSlot;
static TLS BOOL hasCachedRegistrationSlot;
static TLS RegistrationSlot* callbackRegistration;
struct OwnedHook { DWORD tid,pid,slot;ULONGLONG birth;HHOOK message,call; };
static std::vector<OwnedHook> ownedHooks;
static void CloseRegistrationViews() {
    if(GetCurrentProcessId()==ownerPid && GetCurrentThreadId()==registrationOwnerThread) {
        RegistrationView* item=ownerRegistrationViews;InterlockedExchangePointer((PVOID volatile*)&ownerRegistrationViews,0);
        while(item) {RegistrationView* next=item->next;UnmapViewOfFile(item->page);CloseHandle(item->mapping);HeapFree(GetProcessHeap(),0,item);item=next;}
    }
    while(registrationViews) {
        RegistrationView* item=registrationViews;registrationViews=item->next;
        UnmapViewOfFile(item->page);CloseHandle(item->mapping);HeapFree(GetProcessHeap(),0,item);
    }
    registrationViewOwner=registrationViewEpoch=0;hasCachedRegistrationSlot=FALSE;
}
static void RegistrationName(DWORD index,WCHAR* name) {
    wsprintfW(name,L"Local\\TypeTrail.Input.%lu.%lu.%lu",ownerPid,generation,index);
}
static RegistrationPage* GetRegistrationPage(DWORD index,bool create=false) {
    bool owner=GetCurrentProcessId()==ownerPid;
    if(owner) {
        RegistrationView* head=(RegistrationView*)InterlockedCompareExchangePointer((PVOID volatile*)&ownerRegistrationViews,0,0);
        for(RegistrationView* view=head;view;view=view->next)if(view->index==index)return view->page;
        // The dedicated LL thread only consumes owner-published mappings.
        // A new page is opened later by the owner controller during UI retry.
        if(GetCurrentThreadId()!=registrationOwnerThread)return 0;
    }
    if(registrationViewOwner!=ownerPid || registrationViewEpoch!=generation) {
        CloseRegistrationViews();registrationViewOwner=ownerPid;registrationViewEpoch=generation;
    }
    for(RegistrationView* view=registrationViews;view;view=view->next)if(view->index==index)return view->page;
    WCHAR name[128];RegistrationName(index,name);
    HANDLE mapping=create?CreateFileMappingW(INVALID_HANDLE_VALUE,0,PAGE_READWRITE,0,sizeof(RegistrationPage),name):OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
    if(!mapping)return 0;
    RegistrationPage* page=(RegistrationPage*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(RegistrationPage));
    if(!page) { CloseHandle(mapping);return 0; }
    RegistrationView* view=(RegistrationView*)HeapAlloc(GetProcessHeap(),0,sizeof(RegistrationView));
    if(!view) { UnmapViewOfFile(page);CloseHandle(mapping);SetLastError(ERROR_NOT_ENOUGH_MEMORY);return 0; }
    view->index=index;view->mapping=mapping;view->page=page;
    if(owner) {view->next=ownerRegistrationViews;InterlockedExchangePointer((PVOID volatile*)&ownerRegistrationViews,view);}
    else {view->next=registrationViews;registrationViews=view;}
    return page;
}
static RegistrationSlot* RegistrationAt(DWORD index) {
    if(index/RegistrationPageEntries>=registrationPageCount)return 0;
    RegistrationPage* page=GetRegistrationPage(index/RegistrationPageEntries);
    return page?&page->entries[index%RegistrationPageEntries]:0;
}
static DWORD EnsureRegistrationCapacity(DWORD entries) {
    DWORD pages=entries/RegistrationPageEntries+(entries%RegistrationPageEntries?1:0);
    for(DWORD index=registrationPageCount;index<pages;index++) {
        if(!GetRegistrationPage(index,true)) { DWORD error=GetLastError();return error?error:ERROR_NOT_ENOUGH_MEMORY; }
        MemoryBarrier();DWORD current=registrationPageCount;
        while(current<index+1) {DWORD previous=(DWORD)InterlockedCompareExchange((volatile LONG*)&registrationPageCount,(LONG)(index+1),(LONG)current);if(previous==current)break;current=previous;}
    }
    return ERROR_SUCCESS;
}
static ULONGLONG ThreadBirth(HANDLE handle) {
    FILETIME created,exited,kernel,user;
    return GetThreadTimes(handle,&created,&exited,&kernel,&user)?((ULONGLONG)created.dwHighDateTime<<32)|created.dwLowDateTime:0;
}
static RegistrationSlot* CurrentRegistration() {
    DWORD tid=GetCurrentThreadId(),pid=GetCurrentProcessId();
    if(callbackRegistration)return callbackRegistration->activeTid==tid && callbackRegistration->activePid==pid && callbackRegistration->epoch==generation?callbackRegistration:0;
    if(hasCachedRegistrationSlot) {
        RegistrationSlot* slot=RegistrationAt(cachedRegistrationSlot);
        if(slot && slot->activeTid==tid && slot->activePid==pid && slot->epoch==generation)return slot;
        hasCachedRegistrationSlot=FALSE;
    }
    ULONGLONG birth=ThreadBirth(GetCurrentThread());
    for(DWORD i=0;i<registrationSlotCount;i++) {
        RegistrationSlot* slot=RegistrationAt(i);
        if(slot && slot->activeTid==tid && slot->activePid==pid && slot->epoch==generation && slot->threadBirth==birth) {
            cachedRegistrationSlot=i;hasCachedRegistrationSlot=TRUE;return slot;
        }
    }
    return 0;
}
struct RegistrationCallbackScope {
    RegistrationSlot* previous;
    RegistrationCallbackScope():previous(callbackRegistration) {if(!callbackRegistration)callbackRegistration=CurrentRegistration();}
    ~RegistrationCallbackScope() {callbackRegistration=previous;}
};
static bool TargetAllowed(DWORD pid) {
    for(DWORD i=0;i<targetCount;i++) {RegistrationSlot* slot=RegistrationAt(i);if(slot && slot->targetPid==pid)return true;}
    return false;
}
