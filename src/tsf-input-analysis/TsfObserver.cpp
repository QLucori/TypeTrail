#include "TsfObserver.h"
#include <msctf.h>
#include <inputscope.h>
#include <vector>
#include <algorithm>
#include <new>

namespace TTsf {
static __declspec(thread) DWORD diagnostics[24]={};
static const GUID InputScopeGuid={0x1713dd5a,0x68e7,0x4a5b,{0x9a,0xf6,0x59,0x2a,0x59,0x5c,0x77,0x8d}};
template<class T> void Drop(T*& value) { if (value) { value->Release(); value = 0; } }
static void ReleaseRanges(std::vector<ITfRange*>& ranges) { for(auto* range:ranges)range->Release();ranges.clear(); }
static bool Nonempty(TfEditCookie cookie,ITfRange* range) {
    BOOL empty=TRUE;return range && SUCCEEDED(range->IsEmpty(cookie,&empty)) && !empty;
}
static ITfRange* Intersection(TfEditCookie cookie,ITfRange* base,ITfRange* clip) {
    LONG comparison=0;
    if(FAILED(base->CompareEnd(cookie,clip,TF_ANCHOR_START,&comparison)) || comparison<=0)return 0;
    if(FAILED(base->CompareStart(cookie,clip,TF_ANCHOR_END,&comparison)) || comparison>=0)return 0;
    ITfRange* result=0;if(FAILED(base->Clone(&result)) || !result)return 0;
    if(FAILED(result->CompareStart(cookie,clip,TF_ANCHOR_START,&comparison))) { result->Release();return 0; }
    if(comparison<0 && FAILED(result->ShiftStartToRange(cookie,clip,TF_ANCHOR_START))) { result->Release();return 0; }
    if(FAILED(result->CompareEnd(cookie,clip,TF_ANCHOR_END,&comparison))) { result->Release();return 0; }
    if(comparison>0 && FAILED(result->ShiftEndToRange(cookie,clip,TF_ANCHOR_END))) { result->Release();return 0; }
    if(!Nonempty(cookie,result)){result->Release();return 0;}return result;
}
static std::vector<ITfRange*> Subtract(TfEditCookie cookie,ITfRange* original,const std::vector<ITfRange*>& masks) {
    std::vector<ITfRange*> parts;ITfRange* first=0;
    if(FAILED(original->Clone(&first)) || !first)return parts;
    parts.push_back(first);
    for(auto* mask:masks) {
        std::vector<ITfRange*> next;
        for(auto* part:parts) {
            LONG before=0,after=0;
            if(FAILED(part->CompareEnd(cookie,mask,TF_ANCHOR_START,&before)) || FAILED(part->CompareStart(cookie,mask,TF_ANCHOR_END,&after))) { part->Release();continue; }
            if(before<=0 || after>=0) { next.push_back(part);continue; }
            ITfRange* left=0;ITfRange* right=0;
            if(SUCCEEDED(part->Clone(&left)) && left) {
                if(SUCCEEDED(left->ShiftEndToRange(cookie,mask,TF_ANCHOR_START)) && Nonempty(cookie,left))next.push_back(left);else left->Release();
            }
            if(SUCCEEDED(part->Clone(&right)) && right) {
                if(SUCCEEDED(right->ShiftStartToRange(cookie,mask,TF_ANCHOR_END)) && Nonempty(cookie,right))next.push_back(right);else right->Release();
            }
            part->Release();
        }
        parts.swap(next);
        if(parts.size()>128){ReleaseRanges(parts);break;}
    }
    return parts;
}
static bool ActiveCompositionRanges(ITfContext* context,std::vector<ITfRange*>& ranges,TfEditCookie cookie=0,bool readLocked=false) {
    ITfContextComposition* services=0;
    if(FAILED(context->QueryInterface(__uuidof(ITfContextComposition),reinterpret_cast<void**>(&services))) || !services)return false;
    IEnumITfCompositionView* enumeration=0;
    HRESULT hr=services->EnumCompositions(&enumeration);services->Release();
    if(FAILED(hr) || !enumeration)return false;
    bool complete=false,unknownExtent=false;
    for(unsigned i=0;i<64;i++) {
        ITfCompositionView* view=0;ULONG fetched=0;hr=enumeration->Next(1,&view,&fetched);
        if(hr==S_FALSE || !fetched){complete=SUCCEEDED(hr);break;}
        if(FAILED(hr) || !view)break;
        ITfRange* range=0;if(SUCCEEDED(view->GetRange(&range)) && range) {
            bool known=false,nonempty=false;
            if(readLocked) { BOOL empty=TRUE;known=SUCCEEDED(range->IsEmpty(cookie,&empty));nonempty=!empty; }
            else {
                // Poll has no edit cookie. ACP is an optional metadata shortcut,
                // never a requirement for text reads. Collapsed views are not IME preedit.
                ITfRangeACP* acp=0;
                if(SUCCEEDED(range->QueryInterface(__uuidof(ITfRangeACP),reinterpret_cast<void**>(&acp))) && acp) {
                    LONG start=0,length=0;known=SUCCEEDED(acp->GetExtent(&start,&length));nonempty=length>0;acp->Release();
                }
            }
            if(known && nonempty)ranges.push_back(range);else {range->Release();if(!known)unknownExtent=true;}
        }else { view->Release();break; }
        view->Release();
    }
    enumeration->Release();return complete && !unknownExtent;
}
static std::vector<ITfRange*> UpdatedRanges(ITfEditRecord* record,bool textChanges) {
    std::vector<ITfRange*> ranges;const GUID* composing=&GUID_PROP_COMPOSING;
    IEnumTfRanges* enumeration=0;
    if(FAILED(record->GetTextAndPropertyUpdates(textChanges?TF_GTP_INCL_TEXT:0,textChanges?0:&composing,textChanges?0:1,&enumeration)) || !enumeration){diagnostics[13]++;return ranges;}
    for(unsigned i=0;i<64;i++) {
        ITfRange* range=0;ULONG fetched=0;HRESULT hr=enumeration->Next(1,&range,&fetched);
        if(FAILED(hr) || !fetched || !range)break;diagnostics[textChanges?15:14]++;ranges.push_back(range);
    }
    enumeration->Release();return ranges;
}
static bool HasComposition(const VARIANT& value) {
    return (value.vt == VT_I4 && value.lVal != 0) || (value.vt == VT_BOOL && value.boolVal != VARIANT_FALSE);
}
static bool Protected(ITfContext* context, TfEditCookie cookie, ITfRange* range,bool* knownSafe=0) {
    if(knownSafe)*knownSafe=false;
    TF_STATUS status={};
    HRESULT statusHr=context->GetStatus(&status);diagnostics[20]=(DWORD)statusHr;diagnostics[21]=status.dwDynamicFlags;
    if (FAILED(statusHr) || (status.dwDynamicFlags & TS_SD_READONLY)) return true;
    HWND window=GetFocus();
    if (window && (GetWindowLongPtrW(window,GWL_STYLE)&ES_PASSWORD)) {
        WCHAR name[64]={}; GetClassNameW(window,name,64);
        if (_wcsicmp(name,L"Edit")==0 || _wcsnicmp(name,L"RichEdit",8)==0) return true;
    }
    ITfReadOnlyProperty* property=0;
    if (SUCCEEDED(context->GetAppProperty(InputScopeGuid,&property)) && property) {
        VARIANT value; VariantInit(&value);
        if (SUCCEEDED(property->GetValue(cookie,range,&value)) && value.vt==VT_UNKNOWN && value.punkVal) {
            ITfInputScope* scope=0;
            if (SUCCEEDED(value.punkVal->QueryInterface(__uuidof(ITfInputScope),reinterpret_cast<void**>(&scope)))) {
                InputScope* scopes=0; UINT count=0;
                if (SUCCEEDED(scope->GetInputScopes(&scopes,&count))) {
                    bool password=count>128 || (count && !scopes);
                    for(UINT i=0;scopes && i<count && i<128;i++) if(scopes[i]==IS_PASSWORD || scopes[i]==IS_NUMERIC_PASSWORD) password=true;
                    if(knownSafe && scopes && count>0 && count<=128 && !password)*knownSafe=true;
                    CoTaskMemFree(scopes); scope->Release(); VariantClear(&value); property->Release();
                    return password;
                }
                scope->Release();
            }
        }
        VariantClear(&value); property->Release();
    }
    return false;
}
class Observer final : public ITfThreadMgrEventSink, public ITfTextEditSink {
    LONG references_=1;
    ITfThreadMgr* manager_=0;
    ITfSource* managerSource_=0;
    ITfContext* context_=0;
    ITfSource* contextSource_=0;
    DWORD managerCookie_=TF_INVALID_COOKIE, contextCookie_=TF_INVALID_COOKIE;
    CommitCallback callback_=0; void* user_=0;
    EnabledCallback enabled_=0;
    bool Enabled() const { return callback_ && (!enabled_ || enabled_(user_)); }
    bool editing_=false;
    bool compositionKnown_=false, compositionActive_=false;
    std::vector<ITfRange*> composing_;
    void ClearRanges() { for(auto* range:composing_) range->Release(); composing_.clear(); }
    void Unwatch() {
        diagnostics[3]=0;
        if(contextSource_ && contextCookie_!=TF_INVALID_COOKIE) {
            DWORD cookie=contextCookie_; contextCookie_=TF_INVALID_COOKIE; contextSource_->UnadviseSink(cookie);
        }
        Drop(contextSource_); Drop(context_); ClearRanges();compositionKnown_=compositionActive_=false;
    }
    void Bind(ITfDocumentMgr* document) {
        ITfContext* next=0;
        if(document) document->GetTop(&next);
        if(document)diagnostics[2]++;
        if(next==context_) { Drop(next); return; }
        Unwatch(); context_=next;
        if(!context_) return;
        if(FAILED(context_->QueryInterface(__uuidof(ITfSource),reinterpret_cast<void**>(&contextSource_)))) { Unwatch(); return; }
        if(FAILED(contextSource_->AdviseSink(__uuidof(ITfTextEditSink),static_cast<ITfTextEditSink*>(this),&contextCookie_))) Unwatch();
        else { diagnostics[3]=1;diagnostics[4]++; }
    }
    void Refresh() { ITfDocumentMgr* document=0; if(manager_) manager_->GetFocus(&document); Bind(document); Drop(document); }
    void Deliver(ITfContext* context,TfEditCookie cookie,ITfRange* requested,DWORD flags,std::vector<ITfRange*>& sent) {
        if(!Enabled() || !Nonempty(cookie,requested))return;
        auto remaining=Subtract(cookie,requested,sent);
        for(auto* selected:remaining) {
            if(sent.size()>=64 || !Enabled()){selected->Release();continue;}
            bool knownSafe=false;
            if(Protected(context,cookie,selected,&knownSafe)){diagnostics[12]++;selected->Release();continue;}
            ITfRange* covered=0;selected->Clone(&covered);
            // The product can retain at most 500000 UTF-16 units. Start from
            // the END of an oversized changed range so rolling retention keeps
            // its real tail, rather than the tail of an arbitrary read prefix.
            // Keep `covered` as the original span: a later overlapping update
            // must not re-import the head deliberately discarded here.
            bool cropped=false;
            ITfRange* tail=0;
            if(SUCCEEDED(selected->Clone(&tail)) && tail) {
                LONG moved=0,compare=0;
                bool bounded=SUCCEEDED(tail->Collapse(cookie,TF_ANCHOR_END)) &&
                    SUCCEEDED(tail->ShiftStart(cookie,-500000,&moved,0)) &&
                    SUCCEEDED(tail->CompareStart(cookie,selected,TF_ANCHOR_START,&compare));
                if(bounded && compare<0)bounded=SUCCEEDED(tail->ShiftStartToRange(cookie,selected,TF_ANCHOR_START));
                if(bounded) { cropped=compare>0;selected->Release();selected=tail;tail=0; }
                Drop(tail);
            }
            if(cropped)diagnostics[23]++;
            bool delivered=false,firstChunk=true;
            // GetText's MOVESTART cursor reads only this changed/intersected
            // range. Generic ITfRange anchors work without ITfRangeACP.
            for(unsigned chunk=0;chunk<245 && Enabled() && Nonempty(cookie,selected);chunk++) {
                ITfRange* previous=0;if(FAILED(selected->Clone(&previous)) || !previous)break;
                WCHAR buffer[2048];ULONG length=0;
                HRESULT hr=selected->GetText(cookie,TF_TF_MOVESTART,buffer,2048,&length);
                diagnostics[22]=(DWORD)hr;
                if(FAILED(hr) || !length){diagnostics[FAILED(hr)?16:17]++;previous->Release();break;}
                diagnostics[18]++;
                ULONG offset=0;
                if(firstChunk && cropped && IS_LOW_SURROGATE(buffer[0]))offset=1;
                firstChunk=false;
                if(length>offset && Enabled()){diagnostics[(flags&2)?6:7]++;callback_(ContextWindow(),buffer+offset,length-offset,flags|(knownSafe?4:0),user_);delivered=true;}
                LONG moved=0;bool advanced=SUCCEEDED(selected->CompareStart(cookie,previous,TF_ANCHOR_START,&moved)) && moved>0;
                previous->Release();if(!advanced)break;
            }
            if(Enabled() && Nonempty(cookie,selected))diagnostics[23]++;
            if(delivered && covered)sent.push_back(covered);else Drop(covered);
            selected->Release();
        }
    }
    bool AddPropertyCompositions(ITfProperty* property,TfEditCookie cookie,ITfRange* changed,std::vector<ITfRange*>& active) {
        if(!property)return false;
        IEnumTfRanges* segments=0;bool known=false;
        // Attribute-transition enumeration is optional in real text stores.
        // Its E_FAIL must not discard an otherwise valid text-change range.
        if(SUCCEEDED(property->EnumRanges(cookie,&segments,changed)) && segments) {
            known=true;
            for(unsigned i=0;i<64;i++) {
                ITfRange* segment=0;ULONG fetched=0;HRESULT hr=segments->Next(1,&segment,&fetched);
                if(FAILED(hr)||!fetched||!segment)break;
                VARIANT value;VariantInit(&value);
                if(SUCCEEDED(property->GetValue(cookie,segment,&value)) && HasComposition(value) && active.size()<128)active.push_back(segment);else segment->Release();
                VariantClear(&value);
            }
            segments->Release();
        } else {
            VARIANT value;VariantInit(&value);
            if(SUCCEEDED(property->GetValue(cookie,changed,&value))) {
                known=true;
                if(HasComposition(value) && active.size()<128){ITfRange* copy=0;if(SUCCEEDED(changed->Clone(&copy))&&copy)active.push_back(copy);}
            }
            VariantClear(&value);
        }
        return known;
    }
    void RefreshCompositionMetadata() {
        if(!context_){compositionKnown_=compositionActive_=false;return;}
        std::vector<ITfRange*> active;
        if(ActiveCompositionRanges(context_,active)) { compositionKnown_=true;compositionActive_=!active.empty(); }
        ReleaseRanges(active);
    }

public:
    bool HasPendingComposition() const { return compositionKnown_ ? compositionActive_ : !composing_.empty(); }
    HWND ContextWindow() const {
        if(!context_)return 0;
        ITfContextView* view=0;
        HWND window=0;
        if(SUCCEEDED(context_->GetActiveView(&view))&&view) {
            if(FAILED(view->GetWnd(&window)))window=0;
            view->Release();
        }
        return window;
    }
    HRESULT Start(ITfThreadMgr* manager,CommitCallback callback,void* user,EnabledCallback enabled) {
        manager_=manager; manager_->AddRef(); callback_=callback; user_=user; enabled_=enabled;
        HRESULT hr=manager_->QueryInterface(__uuidof(ITfSource),reinterpret_cast<void**>(&managerSource_));
        if(SUCCEEDED(hr)) hr=managerSource_->AdviseSink(__uuidof(ITfThreadMgrEventSink),static_cast<ITfThreadMgrEventSink*>(this),&managerCookie_);
        if(SUCCEEDED(hr))Refresh(); return hr;
    }
    void Stop() {
        Unwatch();
        if(managerSource_&&managerCookie_!=TF_INVALID_COOKIE) { DWORD cookie=managerCookie_;managerCookie_=TF_INVALID_COOKIE;managerSource_->UnadviseSink(cookie); }
        Drop(managerSource_);Drop(manager_);callback_=0;user_=0;
    }
    void Poll(CommitCallback callback,void* user,EnabledCallback enabled) { callback_=callback;user_=user;enabled_=enabled;Refresh();RefreshCompositionMetadata(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {
        if(!out)return E_POINTER;*out=0;
        if(id==__uuidof(IUnknown)||id==__uuidof(ITfThreadMgrEventSink))*out=static_cast<ITfThreadMgrEventSink*>(this);
        else if(id==__uuidof(ITfTextEditSink))*out=static_cast<ITfTextEditSink*>(this);
        else return E_NOINTERFACE;
        AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&references_);}
    ULONG STDMETHODCALLTYPE Release() override {LONG left=InterlockedDecrement(&references_);if(!left)delete this;return left;}
    HRESULT STDMETHODCALLTYPE OnInitDocumentMgr(ITfDocumentMgr*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE OnUninitDocumentMgr(ITfDocumentMgr*) override{Refresh();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnSetFocus(ITfDocumentMgr* focus,ITfDocumentMgr*) override{Bind(focus);return S_OK;}
    HRESULT STDMETHODCALLTYPE OnPushContext(ITfContext*) override{Refresh();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnPopContext(ITfContext*) override{Refresh();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnEndEdit(ITfContext* context,TfEditCookie cookie,ITfEditRecord* record) override {
        diagnostics[5]++;
        if(!Enabled()){diagnostics[9]++;return S_OK;}
        if(editing_){diagnostics[10]++;return S_OK;}
        if(context!=context_){diagnostics[11]++;return S_OK;}
        if(!record)return S_OK;
        diagnostics[8]++;
        editing_=true;
        try {
            std::vector<ITfRange*> sent,active;
            bool compositionKnown=ActiveCompositionRanges(context,active,cookie,true);
            auto properties=UpdatedRanges(record,false);
            auto texts=UpdatedRanges(record,true);
            ITfProperty* property=0;context->GetProperty(GUID_PROP_COMPOSING,&property);
            bool propertyKnown=false;
            if(!compositionKnown) {
                for(auto* range:properties)propertyKnown=AddPropertyCompositions(property,cookie,range,active)||propertyKnown;
                for(auto* range:texts)propertyKnown=AddPropertyCompositions(property,cookie,range,active)||propertyKnown;
            }
            // Metadata-poor providers can still say that a tracked composition
            // remains live. Never use a whole-document fallback to guess it.
            if(!compositionKnown)for(auto* old:composing_)AddPropertyCompositions(property,cookie,old,active);
            compositionKnown_=compositionKnown || propertyKnown;
            if(!compositionKnown_)diagnostics[19]++;
            compositionActive_=!active.empty();
            // Finalization is the changed part of a previously observed
            // composition after removing every still-active composition range.
            for(auto* old:composing_) {
                auto finished=Subtract(cookie,old,active);
                for(auto* part:finished) {
                    for(auto* update:properties) { ITfRange* overlap=Intersection(cookie,part,update);if(overlap){Deliver(context,cookie,overlap,3,sent);overlap->Release();} }
                    for(auto* update:texts) { ITfRange* overlap=Intersection(cookie,part,update);if(overlap){Deliver(context,cookie,overlap,3,sent);overlap->Release();} }
                    part->Release();
                }
            }
            if(compositionKnown || propertyKnown)for(auto* changed:texts) {
                auto plain=Subtract(cookie,changed,active);
                for(auto* part:plain){Deliver(context,cookie,part,1,sent);part->Release();}
            }
            ClearRanges();composing_.swap(active);
            ReleaseRanges(active);ReleaseRanges(sent);ReleaseRanges(properties);ReleaseRanges(texts);Drop(property);
        } catch(...) { /* A host callback must never unwind into TSF. */ }
        editing_=false;return S_OK;
    }
};
static __declspec(thread) Observer* observer=0;
bool ThreadTsfPoll(CommitCallback callback,void* user,EnabledCallback enabled) {
    diagnostics[0]++;
    if(observer){observer->Poll(callback,user,enabled);return true;}
    HMODULE module=GetModuleHandleW(L"msctf.dll");if(!module)return false;
    typedef HRESULT (WINAPI* Getter)(ITfThreadMgr**);
    Getter getter=reinterpret_cast<Getter>(GetProcAddress(module,"TF_GetThreadMgr"));if(!getter)return false;
    ITfThreadMgr* manager=0;if(FAILED(getter(&manager))||!manager)return false;
    diagnostics[1]++;
    Observer* next=new(std::nothrow) Observer();if(!next){manager->Release();return false;}
    HRESULT hr=next->Start(manager,callback,user,enabled);manager->Release();
    if(FAILED(hr)){next->Stop();next->Release();return false;}
    observer=next;return true;
}
void ThreadTsfShutdown(){if(observer){Observer* old=observer;observer=0;old->Stop();old->Release();}}
bool ThreadTsfHasComposition(){return observer && observer->HasPendingComposition();}
HWND ThreadTsfContextWindow(){return observer ? observer->ContextWindow() : 0;}
DWORD ThreadTsfDiagnostics(DWORD* values,DWORD capacity){
    if(!values)return 24;
    DWORD count=(std::min)(capacity,static_cast<DWORD>(24));
    for(DWORD i=0;i<count;i++)values[i]=diagnostics[i];
    return count;
}
}
