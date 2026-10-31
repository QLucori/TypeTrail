#define _WIN32_WINNT 0x0601
#include "bridge.h"
#include <imm.h>
#include <richedit.h>
#include <wchar.h>
#ifdef TT_WITH_TSF
#include "../tsf-input-analysis/TsfObserver.h"
#endif
struct WatchedThread { DWORD tid, pid, epoch; LONG live; };
struct InputActivity { volatile LONG sequence;DWORD pid,epoch;ULONGLONG root,tick; };
struct ForegroundCertificate {volatile LONG sequence;DWORD pid,epoch,policy;ULONGLONG root,focus;};

// The DLL lives in the user's private application directory. Shared image pages
// supply a bounded, non-blocking queue; there are no window messages containing
// pointers, files, sockets, remote reads or waits in a target's input callback.
#ifdef _MSC_VER
#pragma data_seg(".ttshare")
volatile LONG gate = 0, enabled = 0, dropped = 0;
DWORD ownerPid = 0, targetCount = 0;
BOOL includeOwner = FALSE;
volatile LONG readPosition = 0, writePosition = 0, queueWriterRefs = 0;
volatile LONG queueSequences[TT_CAPACITY] = {};
TT_EVENT queue[TT_CAPACITY] = {};
DWORD registrationPageCount = 0, registrationSlotCount = 0;
volatile LONG bootstrapEnabled=0;
DWORD bootstrapPolicyRevision=0,bootstrapPolicyChars=0;
volatile LONG bootstrapPolicySequence=0;
ForegroundCertificate bootstrapForeground={};
InputActivity bootstrapKeyboard={};
DWORD generation = 0;
volatile LONG stats[41]={};


volatile LONG flushSequence=0;
#pragma data_seg()
#pragma comment(linker, "/SECTION:.ttshare,RWS")
#else
#define SHARED __attribute__((section(".ttshare"), shared))
volatile LONG gate SHARED = 0, enabled SHARED = 0, dropped SHARED = 0;
DWORD ownerPid SHARED = 0, targetCount SHARED = 0;
BOOL includeOwner SHARED = FALSE;
volatile LONG readPosition SHARED = 0, writePosition SHARED = 0, queueWriterRefs SHARED = 0;
volatile LONG queueSequences[TT_CAPACITY] SHARED = {};
TT_EVENT queue[TT_CAPACITY] SHARED = {};
DWORD registrationPageCount SHARED = 0, registrationSlotCount SHARED = 0;
volatile LONG bootstrapEnabled SHARED=0;
DWORD bootstrapPolicyRevision SHARED=0,bootstrapPolicyChars SHARED=0;
volatile LONG bootstrapPolicySequence SHARED=0;
ForegroundCertificate bootstrapForeground SHARED={};
InputActivity bootstrapKeyboard SHARED={};
DWORD generation SHARED = 0;
volatile LONG stats[41] SHARED={};


volatile LONG flushSequence SHARED=0;
#endif
static HWND NativeForegroundWindow() {
    return GetForegroundWindow();
}
static HINSTANCE module;


#ifdef _MSC_VER
#define TLS __declspec(thread)
#else
#define TLS __thread
#endif
#include "registration-pages.h"
#include "activity-channel.h"
static TLS WCHAR ignoreChars[4096];
static const DWORD MaximumCommitLength=500000;
// Large results are exceptional. Ordinary input retains the small TLS buffer.
static TLS WCHAR* largeIgnoreChars;
static TLS DWORD largeIgnoreCapacity;
static TLS DWORD ignoreCount, ignorePosition;
static TLS DWORD ignoreSource;
static TLS ULONGLONG ignoreTick;
static TLS BOOL composing;
static TLS HWND pendingSelectAll;
static TLS DWORD pendingSelectGeneration;
static DWORD EditFlags(UINT key,bool ctrl,bool shift) {return 65536u|((key&255u)<<17)|(ctrl?(1u<<25):0)|(shift?(1u<<26):0);}
static TLS HWND inputWindow;
static TLS DWORD ownEpoch;
static TLS WCHAR pendingChar;
static TLS WCHAR pendingText[TT_MAX_TEXT];
static TLS DWORD pendingRepeat;
static TLS HWND pendingWindow;
static TLS BOOL pendingCharacter;
static TLS DWORD pendingGeneration;
static TLS DWORD pendingInputSerial;
static TLS ULONGLONG pendingTick;
static TLS BOOL suppressSubmitReturn;
static TLS ULONGLONG submitReturnTick;
static TLS ULONGLONG keyboardAt;
static TLS DWORD inputSerial, lastImmSerial;
static TLS BOOL compositionAuthorized, tsfCompositionSeen;
static TLS DWORD compositionInputSerial;
static TLS ULONGLONG compositionEndedAt;
static TLS LONG externalSequence;
static TLS ULONGLONG lastMetadataPoll;
static TLS MSG queuedIme;
static TLS BOOL queuedImeValid;
static TLS bool queuedPasteHint;
static TLS ULONGLONG queuedPasteAt;
static ULONGLONG InputNow() {
    return GetTickCount64();
}
static void ClearCompositionAuthorization() { compositionAuthorized=tsfCompositionSeen=FALSE;compositionEndedAt=0;compositionInputSerial=0; }
static bool RecentKeyboard() { ULONGLONG now=InputNow();return keyboardAt && now>=keyboardAt && now-keyboardAt<5000; }
static bool AuthorizedComposition() { ULONGLONG now=InputNow();return compositionAuthorized && (!compositionEndedAt || (now>=compositionEndedAt && now-compositionEndedAt<1000)); }
static void BeginAuthorizedComposition() {
    compositionAuthorized=RecentKeyboard() && inputSerial && inputSerial!=compositionInputSerial;
    compositionInputSerial=inputSerial;compositionEndedAt=0;
}
static void ObserveTsfComposition(bool active) {
    if(active && !tsfCompositionSeen && !composing)BeginAuthorizedComposition();
    if(active && !compositionAuthorized && RecentKeyboard() && inputSerial && inputSerial!=compositionInputSerial)BeginAuthorizedComposition();
    if(!active && tsfCompositionSeen && compositionAuthorized && !compositionEndedAt)compositionEndedAt=InputNow();
    tsfCompositionSeen=active?TRUE:FALSE;
}
static const WCHAR* IgnoreText() { return largeIgnoreChars?largeIgnoreChars:ignoreChars; }
static void ClearIgnore() {
    if(largeIgnoreChars) { SecureZeroMemory(largeIgnoreChars,largeIgnoreCapacity*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,largeIgnoreChars);largeIgnoreChars=0;largeIgnoreCapacity=0; }
    SecureZeroMemory(ignoreChars,sizeof(ignoreChars));ignoreCount=ignorePosition=ignoreSource=0;ignoreTick=0;
}
static bool RememberIgnore(const WCHAR* text,DWORD length) {
    WCHAR* large=0;
    if(length>_countof(ignoreChars)) {
        large=(WCHAR*)HeapAlloc(GetProcessHeap(),0,length*sizeof(WCHAR));
        if(!large)return false;
        CopyMemory(large,text,length*sizeof(WCHAR));
    }
    ClearIgnore();
    if(large) { largeIgnoreChars=large;largeIgnoreCapacity=length; }
    else CopyMemory(ignoreChars,text,length*sizeof(WCHAR));
    ignoreCount=length;return true;
}
static bool pinned;
static bool BootstrapSubscribed();
static bool BootstrapAllowed(HWND);
static bool BootstrapProcessAllowed();
static bool BootstrapCertified();
static bool KnownNativeEditable(HWND);
static bool NativePasteOwns(HWND);
static bool NativeOwnerAlive();
static void StopBootstrap();
static LRESULT CALLBACK MessageHook(int,WPARAM,LPARAM);
static LRESULT CALLBACK CallHook(int,WPARAM,LPARAM);
static LRESULT MessageHookCore(int,WPARAM,LPARAM,bool);
static LRESULT CallHookCore(int,WPARAM,LPARAM,bool);
static void FlushCharacter();
static void WakePendingCharacter();
static void ClearNativePaste();
static void Commit(HWND hwnd, const WCHAR* text, DWORD length,DWORD flags=2);
static bool Eligible(HWND hwnd);
static HWND FocusedThreadWindow() {
    HWND foreground=NativeForegroundWindow();DWORD foregroundPid=0;
    GetWindowThreadProcessId(foreground,&foregroundPid);
    GUITHREADINFO gui={};gui.cbSize=sizeof(gui);
    if(!GetGUIThreadInfo(GetCurrentThreadId(),&gui) || !gui.hwndFocus)return 0;
    DWORD focusPid=0;DWORD focusThread=GetWindowThreadProcessId(gui.hwndFocus,&focusPid);
    if(focusPid!=GetCurrentProcessId() || focusThread!=GetCurrentThreadId() || GetAncestor(gui.hwndFocus,GA_ROOT)!=GetAncestor(foreground,GA_ROOT))return 0;
    return gui.hwndFocus;
}
static bool IsInputMessage(UINT message) {
    return (message>=WM_KEYFIRST && message<=WM_KEYLAST) || message==WM_UNICHAR || message==WM_PASTE || message==WM_IME_STARTCOMPOSITION || message==WM_IME_ENDCOMPOSITION || message==WM_IME_COMPOSITION;
}
static bool InputMessageSharesRoot(HWND source,HWND focus,UINT message) {
    bool ime=message==WM_IME_STARTCOMPOSITION || message==WM_IME_ENDCOMPOSITION || message==WM_IME_COMPOSITION;
    return !ime || (source && focus && GetAncestor(source,GA_ROOT)==GetAncestor(focus,GA_ROOT));
}
static HWND InputTarget(HWND messageWindow,UINT message) {
    if(!IsInputMessage(message))return messageWindow;
    HWND focus=FocusedThreadWindow();
    if(!focus)return messageWindow;
    // IME candidate/helper windows can send internal composition notifications
    // while their shared HIMC still holds the prior word. They are not editor
    // deliveries and must not borrow the focused document's identity.
    if(!InputMessageSharesRoot(messageWindow,focus,message))return messageWindow;
    if(!messageWindow || GetAncestor(messageWindow,GA_ROOT)!=GetAncestor(focus,GA_ROOT))InterlockedIncrement(&stats[9]);
    return focus;
}
static void SyncExternalInput() {
    HWND foreground=NativeForegroundWindow();DWORD pid=0;GetWindowThreadProcessId(foreground,&pid);
    pid=GetCurrentProcessId();
    ULONGLONG root=(ULONGLONG)(ULONG_PTR)GetAncestor(foreground,GA_ROOT),now=GetTickCount64();
    InputActivity* bootstrap=bootstrapEnabled?CurrentBootstrapKeyboard():0;
    for(DWORD i=0;i<(bootstrap?1:targetCount);i++) {
        RegistrationSlot* slot=bootstrap?0:RegistrationAt(i);if(!bootstrap && !slot)continue;InputActivity& input=bootstrap?*bootstrap:slot->activity;
        LONG before=InterlockedCompareExchange(&input.sequence,0,0);
        if(before&1)continue;
        DWORD process=input.pid,epoch=input.epoch;
        ULONGLONG window=input.root,tick=input.tick;
        LONG after=InterlockedCompareExchange(&input.sequence,0,0);
        if(before!=after || process!=pid || epoch!=generation || window!=root || !tick || now-tick>=5000)continue;
        if(before!=externalSequence) { externalSequence=before; ++inputSerial; }
        if(tick>keyboardAt)keyboardAt=tick;
        break;
    }
}
#ifdef TT_WITH_TSF
static void CopyTsfDiagnostics() {
    DWORD values[24]={};TTsf::ThreadTsfDiagnostics(values,24);
    for(int i=0;i<24;i++)InterlockedExchange(&stats[17+i],(LONG)values[i]);
}
static bool __stdcall TsfEnabled(void* user) {
    InterlockedIncrement(&stats[13]);
    if(!enabled || *(DWORD*)user!=generation || !NativeOwnerAlive() || (!CurrentRegistration() && !BootstrapSubscribed()))return false;
    CopyTsfDiagnostics();
    SyncExternalInput();
    HWND target=FocusedThreadWindow();
    if(!target) {
        HWND foreground=NativeForegroundWindow(),context=TTsf::ThreadTsfContextWindow();DWORD pid=0;
        GetWindowThreadProcessId(foreground,&pid);
        DWORD contextPid=0;GetWindowThreadProcessId(context,&contextPid);
        if(contextPid==GetCurrentProcessId() && context && GetAncestor(context,GA_ROOT)==GetAncestor(foreground,GA_ROOT))target=context;
    }
    bool active=TTsf::ThreadTsfHasComposition();ObserveTsfComposition(active);
    bool allowed=(AuthorizedComposition() || (!composing && !active && RecentKeyboard())) && Eligible(target);
    if(allowed)InterlockedIncrement(&stats[14]);return allowed;
}
static void __stdcall TsfCommit(HWND hwnd, const WCHAR* text, DWORD length, DWORD flags, void* user) {
    InterlockedIncrement(&stats[15]);if(flags&2)InterlockedIncrement(&stats[16]);
    if (!TsfEnabled(user)) return;
    HWND focused=FocusedThreadWindow();if(!focused)focused=TTsf::ThreadTsfContextWindow();if(focused)hwnd=focused;
    if(NativePasteOwns(hwnd))return;
    if (pendingCharacter && pendingWindow == hwnd && length == pendingRepeat) {
        bool equal = true; for (DWORD i=0;i<length;i++) if(text[i]!=pendingText[i]) equal=false;
        if(equal) pendingCharacter=FALSE;
    }
    // A preceding IMM result can also be exposed by TSF; suppress that alias.
    bool same = ignoreSource==2 && ignoreCount == length && GetTickCount64()-ignoreTick<500;
    for(DWORD i=0;same && i<length;i++) if(IgnoreText()[i]!=text[i]) same=false;
    if (same) return;
    Commit(hwnd,text,length,4|((flags&2)?8:0)|((flags&4)?1024:0));
}
static void ShutdownThread() {
    TTsf::ThreadTsfShutdown(); ownEpoch=0;
    ClearIgnore();
    suppressSubmitReturn=FALSE;
    ClearCompositionAuthorization();keyboardAt=0;
    ClearNativePaste();
    RegistrationSlot* slot=CurrentRegistration();if(slot)InterlockedExchange(&slot->watched.live,0);
}
static void PollTsf() {
    InterlockedIncrement(&stats[11]);SyncExternalInput();
    if(ownEpoch==generation) { if(TTsf::ThreadTsfPoll(TsfCommit,&ownEpoch,TsfEnabled))InterlockedIncrement(&stats[12]);CopyTsfDiagnostics(); return; }
    RegistrationSlot* slot=CurrentRegistration();if(!slot && !BootstrapSubscribed())return;
    if(slot) {slot->watched.tid=GetCurrentThreadId();slot->watched.pid=GetCurrentProcessId();slot->watched.epoch=generation;InterlockedExchange(&slot->watched.live,1);}
    ownEpoch=generation;
    if(!pinned) { HMODULE keep; pinned=GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(LPCWSTR)&PollTsf,&keep)!=FALSE; }
    if(pinned && TTsf::ThreadTsfPoll(TsfCommit,&ownEpoch,TsfEnabled))InterlockedIncrement(&stats[12]);
    CopyTsfDiagnostics();
}
#endif

static bool Eligible(HWND hwnd) {
    if (!enabled || !hwnd) return false;
    DWORD pid = GetCurrentProcessId();
    if (!includeOwner && pid == ownerPid) return false;
    if(bootstrapEnabled && !BootstrapProcessAllowed())return false;
    bool allowed = TargetAllowed(pid) || BootstrapAllowed(hwnd);
    if (!allowed){InterlockedIncrement(&stats[4]);return false;}
    bool testBypass=includeOwner && pid==ownerPid;
    if (!testBypass && GetAncestor(hwnd, GA_ROOT) != GetAncestor(NativeForegroundWindow(), GA_ROOT)){InterlockedIncrement(&stats[3]);return false;}
    WCHAR cls[128] = {};
    GetClassNameW(hwnd, cls, 128);
    CharUpperBuffW(cls, (DWORD)wcslen(cls));
    // ES_PASSWORD is meaningful for native Edit/RichEdit only. Custom protected
    // editors need an additional parent-side accessibility/allow policy.
    if ((wcscmp(cls, L"EDIT") == 0 || wcsncmp(cls, L"RICHEDIT", 8) == 0 || (wcsncmp(cls, L"WINDOWSFORMS", 12) == 0 && (wcsstr(cls, L".EDIT.") || wcsstr(cls, L".RICHEDIT")))) && (GetWindowLongPtrW(hwnd, GWL_STYLE) & (ES_PASSWORD|ES_READONLY))){InterlockedIncrement(&stats[5]);return false;}
    InterlockedIncrement(&stats[2]);
    return true;
}
static bool Push(HWND hwnd, DWORD kind, const WCHAR* text, DWORD length,DWORD flags=1,bool alreadyAuthorized=false,DWORD serial=MAXDWORD,ULONGLONG tick=0) {
    DWORD acceptedGeneration=generation;
    // A deferred WM_CHAR was already checked in its original foreground editor.
    // Do not drop that accepted tail solely because Alt+Tab moved the foreground.
    if (alreadyAuthorized) {
        if(!enabled || pendingGeneration!=generation)return false;
    } else if (!Eligible(hwnd)) return false;
    // A producer reserves its own slot. A preempted consumer cannot hold a lock
    // which loses unrelated input while the bounded ring still has space.
    InterlockedIncrement(&queueWriterRefs);
    if(!enabled || acceptedGeneration!=generation || !NativeOwnerAlive()) {InterlockedDecrement(&queueWriterRefs);return false;}
    DWORD position=(DWORD)InterlockedCompareExchange(&writePosition,0,0);
    for(;;) {
        LONG sequence=InterlockedCompareExchange(&queueSequences[position%TT_CAPACITY],0,0);
        LONG difference=(LONG)((DWORD)sequence-position);
        if(difference==0) {
            if((DWORD)InterlockedCompareExchange(&writePosition,(LONG)(position+1),(LONG)position)==position)break;
        } else if(difference<0) {
            InterlockedIncrement(&dropped);InterlockedDecrement(&queueWriterRefs);return false;
        }
        position=(DWORD)InterlockedCompareExchange(&writePosition,0,0);YieldProcessor();
    }
    TT_EVENT &event = queue[position % TT_CAPACITY];
    ZeroMemory(&event, sizeof(event)); event.kind = kind; event.pid = GetCurrentProcessId();
    event.tid = GetCurrentThreadId(); event.window = (ULONGLONG)(ULONG_PTR)hwnd; event.tick = tick?tick:(alreadyAuthorized?pendingTick:GetTickCount64());
    event.length = length;
    event.flags=flags;
    if(BootstrapCertified())event.flags|=512;
    if((kind==TT_TEXT || kind==TT_BACKSPACE) && (KnownNativeEditable(hwnd) || (flags&32)))event.flags|=1024;
    event.inputSerial=serial!=MAXDWORD?serial:(alreadyAuthorized?pendingInputSerial:inputSerial);
    if (length) CopyMemory(event.text, text, length * sizeof(WCHAR));
    MemoryBarrier();InterlockedExchange(&queueSequences[position%TT_CAPACITY],(LONG)(position+1));
    InterlockedIncrement(&stats[7]);
    InterlockedDecrement(&queueWriterRefs);
    return true;
}
static void ResetWindow(HWND hwnd) {
    if (inputWindow != hwnd) { inputWindow = hwnd; ClearIgnore();ClearNativePaste(); composing = FALSE;queuedPasteHint=false;suppressSubmitReturn=FALSE;ClearCompositionAuthorization();keyboardAt=0; }
}
// A paste hint is provenance, never saved text. The controller must confirm it
// against the actual edit/TSF result. Reading here avoids the LL keyboard hook
// doing clipboard IO and survives clipboard tools restoring their prior value.
#include "paste-baseline.h"
static bool NativePasteOwns(HWND window) {return nativePasteState.active && nativePasteState.window==window && nativePasteState.epoch==generation;}
static bool __cdecl VerifiedNativePaste(HWND hwnd,const WCHAR* text,DWORD length,DWORD serial,ULONGLONG tick,void*) {
    bool complete=true;
    for(DWORD offset=0;offset<length;) {
        DWORD count=length-offset<TT_MAX_TEXT?length-offset:TT_MAX_TEXT;
        if(offset+count<length && text[offset+count-1]>=0xD800 && text[offset+count-1]<=0xDBFF)--count;
        if(!Push(hwnd,TT_TEXT,text+offset,count,32,false,serial,tick))complete=false;
        offset+=count;
    }
    return complete;
}
static void PasteHint(HWND target) {
    if(!Eligible(target))return;
    PasteBaseline(target);
    if(!OpenClipboard(target))return;
    HGLOBAL memory=(HGLOBAL)GetClipboardData(CF_UNICODETEXT);
    SIZE_T bytes=memory?GlobalSize(memory):0;
    const WCHAR* value=memory?(const WCHAR*)GlobalLock(memory):0;
    if(value && bytes>=sizeof(WCHAR) && bytes<=64*1024*1024) {
        SIZE_T available=bytes/sizeof(WCHAR),length=0;
        while(length<available && value[length])++length;
        if(length && length<available) {
            if(length<=500000)BeginNativePaste(target,value,(DWORD)length,inputSerial);
            if(nativePasteState.active) {
                // The following message proves the synchronous native edit.
                // Avoid duplicating a potentially500k payload as both hint
                // and actual text in the bounded queue.
                PostThreadMessageW(GetCurrentThreadId(),WM_NULL,0,0);
                GlobalUnlock(memory);CloseClipboard();return;
            }
            SIZE_T begin=length>500000?length-500000:0;
            if(begin && value[begin]>=0xDC00 && value[begin]<=0xDFFF)++begin;
            SIZE_T original=begin;DWORD index=0;
            for(SIZE_T offset=begin;offset<length;) {
                DWORD count=(DWORD)((length-offset)<TT_MAX_TEXT?(length-offset):TT_MAX_TEXT);
                if(offset+count<length && value[offset+count-1]>=0xD800 && value[offset+count-1]<=0xDBFF)--count;
                DWORD flags=64 | (offset==original?256:0) | (offset+count==length?128:0) | (index++<<10);
                Push(target,4,value+offset,count,flags);offset+=count;
            }
        }
    }
    if(value)GlobalUnlock(memory);
    CloseClipboard();
}
static void Commit(HWND hwnd, const WCHAR* text, DWORD length,DWORD flags) {
    if (!text || !length || length > MaximumCommitLength) { if (length > MaximumCommitLength) InterlockedIncrement(&dropped); return; }
    // A weak TSF edit notification can arrive between forwarded IMM aliases
    // (observed with Sogou). It must not replace the confirmed result's alias
    // state or import the provider's surrounding range for that same key.
    if ((flags&4) && !(flags&8) && inputSerial && lastImmSerial==inputSerial &&
        ignoreSource==2 && GetTickCount64()-ignoreTick<500) return;
    // IMM can forward one finalized result through the editor, parent and IME
    // windows. Coalesce only aliases in the SAME actual keyboard activity.
    // A later key or composition is a new input, even if the text is identical.
    bool duplicate=flags==2 && inputSerial && lastImmSerial==inputSerial && ignoreSource==2 && ignoreCount==length && GetTickCount64()-ignoreTick<500;
    for(DWORD i=0;duplicate && i<length;i++)if(IgnoreText()[i]!=text[i])duplicate=false;
    if(duplicate) { ignorePosition=0; return; }
    if(flags==2)lastImmSerial=inputSerial;
    // An ordinary TSF changed range is weaker than a confirmed IMM result.
    // Only an explicitly finalized TSF composition may supersede IMM output.
    bool alias=flags==2 && (ignoreSource&8) && ignoreCount==length && GetTickCount64()-ignoreTick<500;
    for(DWORD i=0;alias && i<length;i++)if(IgnoreText()[i]!=text[i])alias=false;
    if(!RememberIgnore(text,length)) { InterlockedIncrement(&dropped);return; }
    ignoreSource=flags;ignoreTick=GetTickCount64();
    if(alias)return;
    for (DWORD offset = 0; offset < length;) {
        DWORD count = length - offset < TT_MAX_TEXT ? length - offset : TT_MAX_TEXT;
        // Keep surrogate pairs together across queue chunks.
        if (offset + count < length && text[offset + count - 1] >= 0xD800 && text[offset + count - 1] <= 0xDBFF) --count;
        Push(hwnd, TT_TEXT, text + offset, count,flags); offset += count;
    }
}
static void Character(HWND hwnd, WCHAR ch, DWORD repeat) {
    ResetWindow(hwnd);
    bool submitted=suppressSubmitReturn && ch==13 && GetTickCount64()-submitReturnTick<500;
    suppressSubmitReturn=FALSE;
    if(submitted)return;
    if(!repeat)repeat=1;
    if (ignorePosition < ignoreCount) {
        while(repeat && ignorePosition<ignoreCount && IgnoreText()[ignorePosition]==ch) { ++ignorePosition;--repeat; }
        if(!repeat)return;
        ClearIgnore();
    }
    if (composing || TTsf::ThreadTsfHasComposition()) return;
    // Backspace is emitted from its key-down event. Chromium and other custom
    // editors may consume that event without generating WM_CHAR; counting both
    // paths would instead delete twice in a conventional Edit control.
    if (ch == 8) return;
    if (ch < 32 && ch != 9 && ch != 13 && ch != 10) return;
    if (ch == 13) ch = 10;
    do {
        DWORD count=repeat>TT_MAX_TEXT?TT_MAX_TEXT:repeat;
        pendingWindow=hwnd;pendingChar=ch;pendingRepeat=count;pendingGeneration=generation;pendingInputSerial=inputSerial;pendingTick=GetTickCount64();pendingCharacter=TRUE;
        for(DWORD i=0;i<count;i++)pendingText[i]=ch;
        repeat-=count;if(repeat)FlushCharacter();
    }while(repeat);
    WakePendingCharacter();
}
static void WakePendingCharacter() {
    RegistrationSlot* slot=CurrentRegistration();
    if(slot)InterlockedExchange64((volatile LONGLONG*)&slot->wakeWindow,(LONGLONG)(ULONG_PTR)pendingWindow);
    // Thread-only messages are excluded by PeekMessage(hwnd=editor).
    // Keep the original wake too for thread/message-loop-only editors.
    if(pendingWindow)PostMessageW(pendingWindow,WM_NULL,0,0);
    PostThreadMessageW(GetCurrentThreadId(),WM_NULL,0,0);
}
static void FlushCharacter() {
    if(!pendingCharacter)return;
    Push(pendingWindow,TT_TEXT,pendingText,pendingRepeat,1,true);pendingCharacter=FALSE;
}
static void CharacterMessage(HWND target,HWND source,WPARAM value,DWORD repeat,UINT message) {
    if(message==WM_UNICHAR) {
        if(value==UNICODE_NOCHAR || value>0x10FFFF || (value>=0xD800 && value<=0xDFFF))return;
        if(value<=0xFFFF)Character(target,(WCHAR)value,repeat);
        else {
            DWORD code=(DWORD)value-0x10000,times=repeat?repeat:1;
            WCHAR pair[2]={(WCHAR)(0xD800+(code>>10)),(WCHAR)(0xDC00+(code&1023))};
            ResetWindow(target);
            for(DWORD i=0;i<times;i++) {
                for(DWORD part=0;part<2;part++) {
                    if(ignorePosition<ignoreCount) {
                        if(IgnoreText()[ignorePosition]==pair[part]) { ++ignorePosition;continue; }
                        ClearIgnore();
                    }
                    if(composing || TTsf::ThreadTsfHasComposition())continue;
                    if(pendingCharacter && pendingRepeat==TT_MAX_TEXT)FlushCharacter();
                    if(!pendingCharacter) {pendingWindow=target;pendingRepeat=0;pendingGeneration=generation;pendingInputSerial=inputSerial;pendingTick=GetTickCount64();pendingCharacter=TRUE;}
                    pendingText[pendingRepeat++]=pair[part];
                }
            }
            if(pendingCharacter)WakePendingCharacter();
        }
        return;
    }
    // This callback is installed through SetWindowsHookExW. Runtime inspection
    // confirms WM_CHAR is UTF-16 here even for GetMessageA/ANSI editors.
    // The target window's encoding must not filter or re-decode hook payloads.
    Character(target,(WCHAR)value,repeat);
}
static bool IsImeMessage(UINT message) { return message==WM_IME_STARTCOMPOSITION || message==WM_IME_ENDCOMPOSITION || message==WM_IME_COMPOSITION; }
static bool SubscribedThread() { return CurrentRegistration()!=0 || BootstrapSubscribed(); }
static void ProcessIme(HWND hwnd,UINT message,WPARAM,LPARAM flags,HWND output=0) {
    if(!IsImeMessage(message))return;
    if(!output)output=hwnd;
    ResetWindow(output);
    SyncExternalInput();
    if(message==WM_IME_STARTCOMPOSITION) {
        // Native editors can forward START through parent/IME windows. A
        // forwarded start within the same active keyboard composition is an
        // alias, not a new no-key composition which revokes its authorization.
        if(!composing || !AuthorizedComposition() || compositionInputSerial!=inputSerial)BeginAuthorizedComposition();
        composing=TRUE;
    }
    if(message==WM_IME_ENDCOMPOSITION) {
        composing=FALSE;
        if(compositionAuthorized && !compositionEndedAt)compositionEndedAt=InputNow();
    }
    if(message!=WM_IME_COMPOSITION || !(flags&GCS_RESULTSTR))return;
    if(!AuthorizedComposition() && (composing || !RecentKeyboard()))return;
    const bool simulated=false;
    HIMC context=simulated?0:ImmGetContext(hwnd);
    HWND contextWindow=hwnd;
    if(!simulated && !context && output!=hwnd){context=ImmGetContext(output);contextWindow=output;}
    if(!simulated && !context)return;
    LONG bytes=0;
    bytes=ImmGetCompositionStringW(context,GCS_RESULTSTR,0,0);
    if(bytes>0 && bytes<=(LONG)(MaximumCommitLength*sizeof(WCHAR)) && bytes%sizeof(WCHAR)==0) {
        WCHAR smallResult[4096];
        WCHAR* result=bytes<=(LONG)sizeof(smallResult)?smallResult:(WCHAR*)HeapAlloc(GetProcessHeap(),0,(SIZE_T)bytes);
        if(result) {
            LONG copied=0;
            copied=ImmGetCompositionStringW(context,GCS_RESULTSTR,result,bytes);
            if(copied==bytes){InterlockedIncrement(&stats[6]);Commit(output,result,(DWORD)(bytes/sizeof(WCHAR)));}
            else InterlockedIncrement(&dropped);
            SecureZeroMemory(result,(SIZE_T)bytes);
            if(result!=smallResult)HeapFree(GetProcessHeap(),0,result);
        } else InterlockedIncrement(&dropped);
    }else if(bytes>(LONG)(MaximumCommitLength*sizeof(WCHAR)))InterlockedIncrement(&dropped);
    if(context)ImmReleaseContext(contextWindow,context);
}
// A controller may publish a registration while this thread is traversing
// its hook chain. Preserve that exact staged forwarding decision so the old
// local hook can finish this message even after publication. Windows may copy
// MSG between hooks, so use its metadata within the active forwarding scope.
struct StagedHookPass {
    StagedHookPass* previous;int code;WPARAM w;LPARAM l;bool call;
    MSG message;CWPSTRUCT sent;
};
static TLS StagedHookPass* stagedHookPass;
static TLS DWORD ownerMessageDispatchDepth,ownerCallDispatchDepth;
struct OwnerHookDispatch {
    DWORD* depth;
    OwnerHookDispatch(DWORD& value,bool bootstrap):depth(bootstrap?0:&value){if(depth)++*depth;}
    ~OwnerHookDispatch(){if(depth)--*depth;}
};
static bool IsStagedHookPass(int code,WPARAM w,LPARAM l,bool call) {
    for(StagedHookPass* p=stagedHookPass;p;p=p->previous) {
        if(p->code!=code || p->w!=w || p->call!=call)continue;
        if(call) {const CWPSTRUCT* m=(const CWPSTRUCT*)l;return p->sent.hwnd==m->hwnd && p->sent.message==m->message && p->sent.wParam==m->wParam && p->sent.lParam==m->lParam;}
        const MSG* m=(const MSG*)l;return p->message.hwnd==m->hwnd && p->message.message==m->message && p->message.wParam==m->wParam && p->message.lParam==m->lParam && p->message.time==m->time && p->message.pt.x==m->pt.x && p->message.pt.y==m->pt.y;
    }
    return false;
}
static LRESULT ForwardStagedHook(int code,WPARAM w,LPARAM l,bool call) {
    if(code!=HC_ACTION || !l)return CallNextHookEx(0,code,w,l);
    StagedHookPass pass={};pass.previous=stagedHookPass;pass.code=code;pass.w=w;pass.l=l;pass.call=call;
    if(call)pass.sent=*(const CWPSTRUCT*)l;else pass.message=*(const MSG*)l;
    stagedHookPass=&pass;
    LRESULT result=CallNextHookEx(0,code,w,l);
    stagedHookPass=pass.previous;return result;
}
#include "bootstrap-hooks.h"
static LRESULT CALLBACK MessageHook(int code,WPARAM w,LPARAM l) {return MessageHookCore(code,w,l,false);}
static LRESULT MessageHookCore(int code, WPARAM w, LPARAM l,bool bootstrapDispatch) {
    HWND deferredImeTarget=0;
    RegistrationCallbackScope registrationScope;
    // An installed owner hook remains staged until its registration is
    // published. Only the actual local bootstrap wrapper may use bootstrap
    // authorization; a staged owner hook must not borrow it.
    if(!CurrentRegistration() && !bootstrapDispatch)return ForwardStagedHook(code,w,l,false);
    OwnerHookDispatch ownerDispatch(ownerMessageDispatchDepth,bootstrapDispatch);
    if(code==HC_ACTION && w==PM_REMOVE) {
        const MSG* refresh=(const MSG*)l;
        if(refresh->message==WM_NULL && refresh->wParam==(WPARAM)(ULONG_PTR)MAXDWORD && refresh->lParam==(LPARAM)(ULONG_PTR)generation) {
            HWND focus=FocusedThreadWindow();if(focus) {RefreshBootstrapHostPolicy(focus);if(BootstrapProcessAllowed() && !NativeProtectedWindow(focus)) {AnnounceBootstrap(focus);PublishBootstrapForeground(focus);}}
        }
    }
    if(code==HC_ACTION && w==PM_REMOVE && !SubscribedThread()) {
        FlushCharacter();
#ifdef TT_WITH_TSF
        if(ownEpoch)ShutdownThread();
#endif
    }
    if (code == HC_ACTION && w == PM_REMOVE && SubscribedThread()) {
        InterlockedIncrement(&stats[0]);
#ifdef TT_WITH_TSF
        if(!enabled || (ownEpoch && ownEpoch!=generation)) ShutdownThread();
#endif
        FlushCharacter();
        queuedImeValid=FALSE;
        const MSG* msg = (const MSG*)l;
        if(pendingSelectAll) {
            HWND selected=pendingSelectAll;pendingSelectAll=0;
            if(pendingSelectGeneration==generation && Eligible(selected)) {
                DWORD start=0,end=0;SendMessageW(selected,EM_GETSEL,(WPARAM)&start,(LPARAM)&end);
                LRESULT length=SendMessageW(selected,WM_GETTEXTLENGTH,0,0);
                bool rich=false;
                if(NativeEditKind(selected,rich) && rich) {
                    GETTEXTLENGTHEX count={GTL_PRECISE|GTL_NUMCHARS,1200};
                    length=SendMessageW(selected,EM_GETTEXTLENGTHEX,(WPARAM)&count,0);
                }
                // RichEdit may include its final paragraph marker in a full
                // selection although that marker is outside the text length.
                bool full=length>0 && start==0 && (ULONGLONG)end>=(ULONGLONG)length && (ULONGLONG)end<=(ULONGLONG)length+(rich?1:0);
                if(full)Push(selected,3,0,0,EditFlags('A',true,false));
                else if(start!=end)Push(selected,3,0,0); // Unknown partial selection: retain the prior fragment.
            }
        }
        if(msg->message==WM_NULL && msg->wParam==(WPARAM)(ULONG_PTR)MAXDWORD && msg->lParam==(LPARAM)(ULONG_PTR)generation) {
            HWND focus=FocusedThreadWindow();if(focus)RefreshBootstrapHostPolicy(focus);
        }
        CompleteNativePaste(msg->message,msg->wParam,VerifiedNativePaste,0);
        if(nativePasteState.active && msg->message==WM_CHAR && msg->wParam==0x16)PostThreadMessageW(GetCurrentThreadId(),WM_NULL,0,0);
        LONG requested=InterlockedCompareExchange(&flushSequence,0,0);
        if(requested && msg->message==WM_NULL && msg->wParam==(WPARAM)(ULONG_PTR)(DWORD)requested && msg->lParam==(LPARAM)(ULONG_PTR)generation) {
            RegistrationSlot* slot=CurrentRegistration();if(slot)InterlockedExchange(&slot->flushAcknowledged,requested);
        }
        HWND target=InputTarget(msg->hwnd,msg->message);
        if (Eligible(target)) {
            bool down=msg->message==WM_KEYDOWN || msg->message==WM_SYSKEYDOWN;
            if(down || msg->message==WM_CHAR)ResetWindow(target);
            if(down) {
                // The current selection key can synchronously finalize TSF/IMM
                // during PollTsf below. Retire the PREVIOUS key's aliases now,
                // never after those callbacks have created the new alias state.
                ClearIgnore();
                SyncExternalInput();
                suppressSubmitReturn=FALSE;
                bool ctrl=(GetKeyState(VK_CONTROL)&0x8000)!=0,alt=(GetKeyState(VK_MENU)&0x8000)!=0,shift=(GetKeyState(VK_SHIFT)&0x8000)!=0;
                if(!alt && ((!ctrl && ((msg->wParam>=48 && msg->wParam<=90) || msg->wParam==VK_SPACE || msg->wParam==VK_RETURN || msg->wParam==VK_BACK || msg->wParam==VK_DELETE || msg->wParam>=186)) || (ctrl && (msg->wParam=='V' || msg->wParam=='X' || msg->wParam=='Z' || msg->wParam=='Y')))) { keyboardAt=InputNow(); ++inputSerial; }
                if(ctrl && !alt && msg->wParam=='A') {
                    if(KnownNativeEditable(target)){pendingSelectAll=target;pendingSelectGeneration=generation;}
                    // An IME may consume Ctrl+A inside its preedit UI. Without
                    // native selection proof, retain an explicitly historical
                    // fragment rather than replacing confirmed text by guess.
                    else Push(target,3,0,0,EditFlags((composing || TTsf::ThreadTsfHasComposition())?0:'A',true,shift));
                }
                if(msg->wParam==VK_BACK && !ctrl && !alt &&
                   !(GetKeyState(VK_LWIN)&0x8000) && !(GetKeyState(VK_RWIN)&0x8000)) {
#ifdef TT_WITH_TSF
                    PollTsf();
#endif
                    if(!composing && !TTsf::ThreadTsfHasComposition()) {
                        DWORD repeat=LOWORD(msg->lParam);if(!repeat)repeat=1;
                        for(DWORD i=0;i<repeat;i++)Push(target,TT_BACKSPACE,0,0);
                    }
                }
                if(!shift && msg->wParam==VK_RETURN && !composing && !TTsf::ThreadTsfHasComposition()) {
                    suppressSubmitReturn=Push(target,3,0,0)?TRUE:FALSE;submitReturnTick=GetTickCount64();
                }
                // Without the editor's caret/selection, do not apply a later
                // edit to an assumed history tail. Candidate navigation is IME
                // work and must leave the typing segment intact.
                bool navigation=msg->wParam==VK_LEFT || msg->wParam==VK_RIGHT || msg->wParam==VK_UP || msg->wParam==VK_DOWN || msg->wParam==VK_HOME || msg->wParam==VK_END || msg->wParam==VK_PRIOR || msg->wParam==VK_NEXT || msg->wParam==VK_DELETE;
                bool editing=ctrl && (msg->wParam==VK_BACK || msg->wParam=='X' || msg->wParam=='Z' || msg->wParam=='Y');
                if(!alt && (navigation || editing) && !composing && !TTsf::ThreadTsfHasComposition())Push(target,3,0,0,EditFlags((UINT)msg->wParam,ctrl,shift));
                if(!alt && ((ctrl && msg->wParam=='V') || (shift && msg->wParam==VK_INSERT))) {
                    PasteHint(target);queuedPasteHint=true;queuedPasteAt=GetTickCount64();
                }
            }
            if(msg->message==WM_CHAR)keyboardAt=InputNow();
#ifdef TT_WITH_TSF
            PollTsf();
#endif
            if(IsImeMessage(msg->message)) {
                InterlockedIncrement(&stats[8]);queuedIme=*msg;queuedImeValid=TRUE;
                // Other GetMessage hooks may publish the current IMM result
                // while CallNextHookEx runs (observed with Sogou). Before the
                // chain returns, this context can still contain the PRIOR word.
                if(msg->message==WM_IME_COMPOSITION && (msg->lParam&GCS_RESULTSTR))deferredImeTarget=target;
                else ProcessIme(msg->hwnd,msg->message,msg->wParam,msg->lParam,target);
            }
            if (msg->message == WM_CHAR || msg->message==WM_UNICHAR) CharacterMessage(target,msg->hwnd,msg->wParam,LOWORD(msg->lParam),msg->message);
            if(msg->message==WM_PASTE) { if(!queuedPasteHint || GetTickCount64()-queuedPasteAt>500)PasteHint(target);queuedPasteHint=false; }
            bool mouseBoundary=msg->message==WM_LBUTTONDOWN || msg->message==WM_LBUTTONDBLCLK || msg->message==WM_RBUTTONDOWN || msg->message==WM_RBUTTONDBLCLK;
            if(mouseBoundary && !composing && !TTsf::ThreadTsfHasComposition()) {
                HWND focus=FocusedThreadWindow();
                if(focus && (msg->hwnd==focus || IsChild(focus,msg->hwnd)))Push(focus,3,0,0);
            }
        }
#ifdef TT_WITH_TSF
        else if(enabled && GetTickCount64()-lastMetadataPoll>=100) {
            lastMetadataPoll=GetTickCount64();
            HWND focus=FocusedThreadWindow();
            if(focus && Eligible(focus))PollTsf();
        }
#endif
    }
    LRESULT next=CallNextHookEx(0, code, w, l);
    if(deferredImeTarget && SubscribedThread() && Eligible(deferredImeTarget)) {
        const MSG* delivered=(const MSG*)l;
        if(delivered->message==WM_IME_COMPOSITION && (delivered->lParam&GCS_RESULTSTR))
            ProcessIme(delivered->hwnd,delivered->message,delivered->wParam,delivered->lParam,deferredImeTarget);
    }
    return next;
}
static LRESULT CALLBACK CallHook(int code,WPARAM w,LPARAM l) {return CallHookCore(code,w,l,false);}
static LRESULT CallHookCore(int code, WPARAM w, LPARAM l,bool bootstrapDispatch) {
    RegistrationCallbackScope registrationScope;
    if(!CurrentRegistration() && !bootstrapDispatch)return ForwardStagedHook(code,w,l,true);
    OwnerHookDispatch ownerDispatch(ownerCallDispatchDepth,bootstrapDispatch);
    if (code == HC_ACTION && SubscribedThread()) {
        InterlockedIncrement(&stats[1]);
#ifdef TT_WITH_TSF
        if(!enabled || (ownEpoch && ownEpoch!=generation)) ShutdownThread();
#endif
        const CWPSTRUCT* msg = (const CWPSTRUCT*)l;
        HWND target=InputTarget(msg->hwnd,msg->message);
        if (Eligible(target)) {
            // WH_CALLWNDPROC observes sent messages; WH_GETMESSAGE observes
            // removed queued messages. They are distinct message deliveries.
            // Do not coalesce equal payloads across later independent commits.
            ProcessIme(msg->hwnd,msg->message,msg->wParam,msg->lParam,target);
            if(msg->message==WM_PASTE) { if(!queuedPasteHint || GetTickCount64()-queuedPasteAt>500)PasteHint(target);queuedPasteHint=false; }
        }
    }
    return CallNextHookEx(0, code, w, l);
}
#include "registration-lifecycle.h"
#include "bootstrap-owner.h"
DWORD __cdecl TT_Read(TT_EVENT* events, DWORD capacity) {
    if (!events || ownerPid != GetCurrentProcessId() || !capacity || capacity > TT_CAPACITY) return 0;
    DWORD count = 0;
    DWORD position=(DWORD)readPosition;
    while (count < capacity) {
        LONG sequence=InterlockedCompareExchange(&queueSequences[position%TT_CAPACITY],0,0);
        if((LONG)((DWORD)sequence-(position+1))!=0)break;
        TT_EVENT &event=queue[position%TT_CAPACITY];events[count++]=event;SecureZeroMemory(&event,sizeof(event));
        MemoryBarrier();InterlockedExchange(&queueSequences[position%TT_CAPACITY],(LONG)(position+TT_CAPACITY));++position;
    }
    if(bootstrapEnabled)for(DWORD i=0;i<count;i++)if(events[i].kind==6 && (events[i].flags&512)) {
        DWORD pid=events[i].pid,tid=events[i].tid;
        UpdateSubscriptions(&pid,1,&tid,1,false);
        HWND window=(HWND)(ULONG_PTR)events[i].window;DWORD actual=0;
        if(GetWindowThreadProcessId(window,&actual)==tid && actual==pid)for(size_t j=0;j<ownedHooks.size();j++)if(ownedHooks[j].tid==tid && ownedHooks[j].pid==pid) {
            RegistrationSlot* slot=RegistrationAt(ownedHooks[j].slot);
            if(slot && slot->epoch==generation)InterlockedExchange64((volatile LONGLONG*)&slot->wakeWindow,(LONGLONG)(ULONG_PTR)window);
            break;
        }
    }
    InterlockedExchange(&readPosition,(LONG)position);return count;
}
DWORD __cdecl TT_Dropped() { return ownerPid == GetCurrentProcessId() ? (DWORD)dropped : 0; }
DWORD __cdecl TT_Stats(DWORD* values,DWORD capacity) { if(ownerPid!=GetCurrentProcessId() || !values || capacity<8)return 0;DWORD count=capacity<41?capacity:41;for(DWORD i=0;i<count;i++)values[i]=(DWORD)stats[i];return count; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    if (reason == DLL_THREAD_DETACH || reason == DLL_PROCESS_DETACH) {
        FlushCharacter();
        ClearIgnore();
        ClearNativePaste();
        CloseRegistrationViews();
        CloseForeignActivityChannel();
        CloseBootstrapPolicyView();if(nativeOwnerHandle){CloseHandle(nativeOwnerHandle);nativeOwnerHandle=0;}
    }
    return TRUE;
}
