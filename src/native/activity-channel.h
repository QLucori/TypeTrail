#pragma once
#include <stddef.h>
#pragma pack(push,8)
struct ActivityChannel {
    DWORD magic,version,bytes,owner,epoch,policy;
    volatile LONG enabled;
    DWORD reserved;
    ForegroundCertificate foreground;
    InputActivity input;
};
#pragma pack(pop)
static_assert(sizeof(ActivityChannel)==96,"Cross-bitness activity channel size");
static_assert(offsetof(ActivityChannel,foreground)==32,"Foreground certificate ABI");
static_assert(offsetof(ActivityChannel,input)==64,"Keyboard activity ABI");
static_assert(offsetof(InputActivity,root)==16 && offsetof(InputActivity,tick)==24,"Activity alignment");
static ActivityChannel* ownedActivityChannel;
static HANDLE activityChannelOwner;
static TLS ActivityChannel* foreignActivityChannel;
static TLS HANDLE foreignActivityMapping;
static TLS DWORD foreignActivityOwner,foreignActivityEpoch;
static void ActivityChannelName(WCHAR* name) {wsprintfW(name,L"Local\\TypeTrail.Activity.%lu.%lu",ownerPid,generation);}
static void CloseForeignActivityChannel() {
    if(foreignActivityChannel)UnmapViewOfFile(foreignActivityChannel);
    if(foreignActivityMapping)CloseHandle(foreignActivityMapping);
    foreignActivityChannel=0;foreignActivityMapping=0;foreignActivityOwner=foreignActivityEpoch=0;
}
static ActivityChannel* CurrentActivityChannel() {
    if(GetCurrentProcessId()==ownerPid)return ownedActivityChannel;
    if(foreignActivityOwner!=ownerPid || foreignActivityEpoch!=generation)CloseForeignActivityChannel();
    if(!foreignActivityChannel && bootstrapEnabled) {
        WCHAR name[128];ActivityChannelName(name);
        HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);if(!mapping)return 0;
        ActivityChannel* view=(ActivityChannel*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(ActivityChannel));
        if(!view || view->magic!=0x54544143 || view->version!=1 || view->bytes!=sizeof(ActivityChannel) || view->owner!=ownerPid || view->epoch!=generation) {
            if(view)UnmapViewOfFile(view);CloseHandle(mapping);return 0;
        }
        foreignActivityMapping=mapping;foreignActivityChannel=view;foreignActivityOwner=ownerPid;foreignActivityEpoch=generation;
    }
    return foreignActivityChannel;
}
static DWORD CreateActivityChannel() {
    if(ownedActivityChannel)return ERROR_SUCCESS;
    WCHAR name[128];ActivityChannelName(name);
    HANDLE mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,0,PAGE_READWRITE,0,sizeof(ActivityChannel),name);
    if(!mapping)return GetLastError();
    ActivityChannel* view=(ActivityChannel*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(ActivityChannel));
    if(!view) {DWORD error=GetLastError();CloseHandle(mapping);return error;}
    ZeroMemory(view,sizeof(*view));view->magic=0x54544143;view->version=1;view->bytes=sizeof(*view);view->owner=ownerPid;view->epoch=generation;view->policy=bootstrapPolicyRevision;
    activityChannelOwner=mapping;ownedActivityChannel=view;return ERROR_SUCCESS;
}
static void CloseOwnedActivityChannel() {
    if(ownedActivityChannel) {InterlockedExchange(&ownedActivityChannel->enabled,0);UnmapViewOfFile(ownedActivityChannel);}
    if(activityChannelOwner)CloseHandle(activityChannelOwner);
    ownedActivityChannel=0;activityChannelOwner=0;
}
static ForegroundCertificate* CurrentForegroundCertificate() {ActivityChannel* channel=CurrentActivityChannel();return channel?&channel->foreground:&bootstrapForeground;}
static InputActivity* CurrentBootstrapKeyboard() {ActivityChannel* channel=CurrentActivityChannel();return channel?&channel->input:&bootstrapKeyboard;}
BOOL __cdecl TT_WriteActivity(void* mapped,DWORD pid,ULONGLONG window,ULONGLONG tick) {
    ActivityChannel* channel=(ActivityChannel*)mapped;
    if(!channel || !pid || !window || channel->magic!=0x54544143 || channel->version!=1 || channel->bytes!=sizeof(ActivityChannel) || !channel->owner || !channel->epoch || !channel->enabled)return FALSE;
    ULONGLONG now=GetTickCount64();if(!tick || tick>now || now-tick>=5000)return FALSE;
    HWND hwnd=(HWND)(ULONG_PTR)window;DWORD actual=0;GetWindowThreadProcessId(hwnd,&actual);
    ULONGLONG root=(ULONGLONG)(ULONG_PTR)GetAncestor(hwnd,GA_ROOT);
    if(actual!=pid || root!=(ULONGLONG)(ULONG_PTR)GetAncestor(NativeForegroundWindow(),GA_ROOT))return FALSE;
    ForegroundCertificate& certificate=channel->foreground;
    LONG before=InterlockedCompareExchange(&certificate.sequence,0,0);
    DWORD certifiedPid=certificate.pid,epoch=certificate.epoch,policy=certificate.policy;
    ULONGLONG certifiedRoot=certificate.root,focus=certificate.focus;
    LONG after=InterlockedCompareExchange(&certificate.sequence,0,0);
    if((before&1) || before!=after || certifiedPid!=pid || epoch!=channel->epoch || policy!=channel->policy || certifiedRoot!=root || focus!=window || !channel->enabled)return FALSE;
    InputActivity& input=channel->input;before=InterlockedCompareExchange(&input.sequence,0,0);
    if((before&1) || InterlockedCompareExchange(&input.sequence,before+1,before)!=before)return FALSE;
    if(input.pid==pid && input.epoch==epoch && input.root==root && input.tick>tick) {InterlockedExchange(&input.sequence,before);return TRUE;}
    input.pid=pid;input.epoch=epoch;input.root=root;input.tick=tick;
    InterlockedExchange(&input.sequence,before+2);return TRUE;
}
DWORD __cdecl TT_GetActivityChannelName(WCHAR* output,DWORD capacity) {
    if(GetCurrentProcessId()!=ownerPid || !enabled || !bootstrapEnabled || !ownedActivityChannel)return ERROR_INVALID_STATE;
    WCHAR name[128];ActivityChannelName(name);DWORD length=(DWORD)wcslen(name)+1;
    if(!output || capacity<length)return ERROR_INSUFFICIENT_BUFFER;
    CopyMemory(output,name,length*sizeof(WCHAR));return ERROR_SUCCESS;
}
