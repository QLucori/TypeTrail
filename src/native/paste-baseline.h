// Included after Push: only numeric metadata leaves this transient native read.
#ifndef TYPETRAIL_NATIVE_PASTE_BASELINE_H
#define TYPETRAIL_NATIVE_PASTE_BASELINE_H
#include <windows.h>
#include <wchar.h>

typedef bool (__cdecl* NativePasteOutput)(HWND,const WCHAR*,DWORD,DWORD,ULONGLONG,void*);
struct NativePasteState {
    HWND window;
    DWORD length,start,end,serial,epoch,clipboardLength;
    ULONGLONG tick,prefixHash,suffixHash;
    WCHAR* clipboard;
    bool active;
};
static TLS NativePasteState nativePasteState={};
static TLS bool nativePasteBusy=false;

static void ClearNativePaste() {
    if(nativePasteState.clipboard) {
        SecureZeroMemory(nativePasteState.clipboard,((SIZE_T)nativePasteState.clipboardLength+1)*sizeof(WCHAR));
        HeapFree(GetProcessHeap(),0,nativePasteState.clipboard);
    }
    ZeroMemory(&nativePasteState,sizeof(nativePasteState));
}
// Unknown/custom controls keep TSF-based confirmation, not equality-only paste.
static bool NativeEditKind(HWND window,bool& rich) {
    WCHAR name[128]={};GetClassNameW(window,name,_countof(name));CharUpperBuffW(name,(DWORD)wcslen(name));
    rich=wcsncmp(name,L"RICHEDIT",8)==0 || (wcsncmp(name,L"WINDOWSFORMS10.",15)==0 && wcsstr(name,L".RICHEDIT"));
    return rich || wcscmp(name,L"EDIT")==0 || wcsncmp(name,L"WINDOWSFORMS10.EDIT.",20)==0;
}
static DWORD NormalizedPrefixLength(const WCHAR* text,DWORD length) {
    DWORD count=0;for(DWORD i=0;i<length;i++){++count;if(text[i]==L'\r' && i+1<length && text[i+1]==L'\n')++i;}return count;
}
static DWORD NormalizeNativePaste(WCHAR* text,DWORD length) {
    DWORD output=0;
    for(DWORD i=0;i<length;i++) {
        WCHAR value=text[i];
        if(value==L'\r') {value=L'\n';if(i+1<length && text[i+1]==L'\n')i++;}
        text[output++]=value;
    }
    SecureZeroMemory(text+output,((SIZE_T)length-output+1)*sizeof(WCHAR));return output;
}
static ULONGLONG NativePasteHash(const WCHAR* text,DWORD length) {
    ULONGLONG hash=14695981039346656037ULL;
    for(DWORD i=0;i<length;i++) {hash^=(ULONGLONG)text[i];hash*=1099511628211ULL;}
    return hash;
}
static bool NativePasteIdentity(HWND window,bool requireFocus) {
    DWORD pid=0;DWORD thread=GetWindowThreadProcessId(window,&pid);
    if(!window || pid!=GetCurrentProcessId() || thread!=GetCurrentThreadId())return false;
    if(requireFocus && (GetFocus()!=window || GetAncestor(window,GA_ROOT)!=GetAncestor(GetForegroundWindow(),GA_ROOT)))return false;
    bool rich=false;
    return NativeEditKind(window,rich) && !(GetWindowLongPtrW(window,GWL_STYLE)&(ES_PASSWORD|ES_READONLY));
}
static WCHAR* ReadNativePasteEditor(HWND window,DWORD& length,DWORD& start,DWORD& end) {
    length=start=end=0;
    bool rich=false;if(!NativeEditKind(window,rich))return 0;
    if(GetWindowLongPtrW(window,GWL_STYLE)&(ES_PASSWORD|ES_READONLY))return 0;
    int expected=GetWindowTextLengthW(window);if(expected<0 || expected>500000)return 0;
    WCHAR* value=(WCHAR*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,((SIZE_T)expected+1)*sizeof(WCHAR));
    if(!value)return 0;
    SendMessageW(window,EM_GETSEL,(WPARAM)&start,(LPARAM)&end);
    int copied=GetWindowTextW(window,value,expected+1);
    if(copied!=expected || start>end || (!rich && end>(DWORD)copied)) {
        SecureZeroMemory(value,((SIZE_T)expected+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,value);return 0;
    }
    if(!rich) {start=NormalizedPrefixLength(value,start);end=NormalizedPrefixLength(value,end);}
    length=NormalizeNativePaste(value,(DWORD)copied);
    if(end>length) {SecureZeroMemory(value,((SIZE_T)expected+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,value);return 0;}
    return value;
}
// Called only during an actual target paste command. The caller retains its
// ordinary Kind4 hint flow; this state cannot commit until the edit proves it.
static void BeginNativePaste(HWND window,const WCHAR* clipboard,DWORD length,DWORD serial,bool requireFocus=true) {
    if(nativePasteBusy)return;
    ClearNativePaste();
    if(!clipboard || !length || length>500000 || !NativePasteIdentity(window,requireFocus))return;
    nativePasteBusy=true;
    DWORD beforeLength=0,start=0,end=0;
    WCHAR* before=ReadNativePasteEditor(window,beforeLength,start,end);
    if(before) {
        WCHAR* copy=(WCHAR*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,((SIZE_T)length+1)*sizeof(WCHAR));
        if(copy) {
            CopyMemory(copy,clipboard,(SIZE_T)length*sizeof(WCHAR));
            DWORD normalized=NormalizeNativePaste(copy,length);
            LONG_PTR style=GetWindowLongPtrW(window,GWL_STYLE);
            if(style&ES_UPPERCASE)CharUpperBuffW(copy,normalized);
            else if(style&ES_LOWERCASE)CharLowerBuffW(copy,normalized);
            nativePasteState.window=window;nativePasteState.length=beforeLength;nativePasteState.start=start;nativePasteState.end=end;
            nativePasteState.serial=serial;nativePasteState.epoch=generation;nativePasteState.tick=GetTickCount64();
            nativePasteState.prefixHash=NativePasteHash(before,start);
            nativePasteState.suffixHash=NativePasteHash(before+end,beforeLength-end);
            nativePasteState.clipboard=copy;nativePasteState.clipboardLength=normalized;nativePasteState.active=true;
        }
        SecureZeroMemory(before,((SIZE_T)beforeLength+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,before);
    }
    nativePasteBusy=false;
}
static bool NativePasteNextEdit(UINT message,WPARAM key) {
    if(message==WM_CHAR && key==0x16)return false; // translated Ctrl+V command
    if(message==WM_LBUTTONDOWN || message==WM_RBUTTONDOWN || message==WM_MBUTTONDOWN)return true;
    if(message!=WM_KEYDOWN && message!=WM_SYSKEYDOWN && message!=WM_CHAR && message!=WM_UNICHAR)return false;
    if(key==VK_SHIFT || key==VK_CONTROL || key==VK_MENU || key==VK_LSHIFT || key==VK_RSHIFT || key==VK_LCONTROL || key==VK_RCONTROL || key==VK_LMENU || key==VK_RMENU)return false;
    return true;
}
// Runs before the next queued message is dispatched, so a later literal key
// cannot become part of a guessed paste insertion. No hook callback blocks.
static bool CompleteNativePaste(UINT nextMessage,WPARAM nextKey,NativePasteOutput output,void* user,bool requireFocus=true) {
    if(nativePasteBusy || !nativePasteState.active)return false;
    if(!enabled || nativePasteState.epoch!=generation || GetTickCount64()-nativePasteState.tick>1000 || !NativePasteIdentity(nativePasteState.window,requireFocus)) {ClearNativePaste();return false;}
    nativePasteBusy=true;
    DWORD length=0,start=0,end=0;
    WCHAR* after=ReadNativePasteEditor(nativePasteState.window,length,start,end);
    bool delivered=false,keep=false;
    if(after) {
        LONGLONG inserted=(LONGLONG)length-nativePasteState.length+(nativePasteState.end-nativePasteState.start);
        if(inserted>0 && inserted<=nativePasteState.clipboardLength && start==end &&
            start==(ULONGLONG)nativePasteState.start+inserted && start<=length &&
            NativePasteHash(after,nativePasteState.start)==nativePasteState.prefixHash &&
            NativePasteHash(after+start,length-start)==nativePasteState.suffixHash &&
            wmemcmp(after+nativePasteState.start,nativePasteState.clipboard,(SIZE_T)inserted)==0) {
            if(output)delivered=output(nativePasteState.window,after+nativePasteState.start,(DWORD)inserted,nativePasteState.serial,nativePasteState.tick,user);
        } else if(inserted==0 && !NativePasteNextEdit(nextMessage,nextKey))keep=true;
        SecureZeroMemory(after,((SIZE_T)length+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,after);
    }
    if(!keep)ClearNativePaste();
    nativePasteBusy=false;return delivered;
}
static void PasteBaseline(HWND target) {
    bool rich=false;if(!NativeEditKind(target,rich))return;
    if(GetWindowLongPtrW(target,GWL_STYLE)&(ES_PASSWORD|ES_READONLY))return;
    int length=GetWindowTextLengthW(target);if(length<0 || length>500000)return;
    WCHAR* text=(WCHAR*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,((SIZE_T)length+1)*sizeof(WCHAR));if(!text)return;
    DWORD start=0,end=0;SendMessageW(target,EM_GETSEL,(WPARAM)&start,(LPARAM)&end);
    int read=GetWindowTextW(target,text,length+1);
    DWORD normalized=NormalizedPrefixLength(text,(DWORD)read);
    bool valid=read==length && start<=end;
    if(rich)valid=valid && end<=normalized;
    else if(end<=(DWORD)read){start=NormalizedPrefixLength(text,start);end=NormalizedPrefixLength(text,end);}else valid=false;
    SecureZeroMemory(text,((SIZE_T)length+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,text);
    if(valid) {
        WCHAR value[64]={};int count=swprintf_s(value,_countof(value),L"%lu,%lu,%lu",normalized,start,end);
        if(count>0)Push(target,5,value,(DWORD)count,64);
    }
}
#endif
