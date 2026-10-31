using System;

namespace TypeTrail {
    // Unicode16 extended grapheme clusters (UAX29 GB3..GB13, including GB9c).
    // Streaming state avoids allocating a scalar array for long input cards.
    internal static class GraphemeText {
        internal static int Property(int value,int[] ranges) {
            int lo=0,hi=ranges.Length/3-1;
            while(lo<=hi) {
                int middle=(lo+hi)/2,offset=middle*3;
                if(value<ranges[offset])hi=middle-1;
                else if(value>ranges[offset+1])lo=middle+1;
                else return ranges[offset+2];
            }
            return 0;
        }
        static int Gcb(int scalar) {
            if(scalar>=0xAC00 && scalar<=0xD7A3)return (scalar-0xAC00)%28==0?12:13;
            return Property(scalar,GraphemeData.Gcb);
        }
        internal static int Scan(string text,Action<int> boundary) {
            if(String.IsNullOrEmpty(text))return 0;
            int previous=0,regional=0,last=0;
            bool first=true,pictographicRun=false,pictographicZwj=false,consonant=false,linker=false;
            for(int index=0;index<text.Length;) {
                int start=index,scalar=text[index++];
                if(Char.IsHighSurrogate((char)scalar) && index<text.Length && Char.IsLowSurrogate(text[index]))scalar=Char.ConvertToUtf32((char)scalar,text[index++]);
                int current=scalar<0x7F?(scalar==13?1:scalar==10?2:scalar<32 || scalar==127?3:0):Gcb(scalar);
                int indic=scalar<0x300?0:Property(scalar,GraphemeData.Conjunct);
                bool pictographic=scalar>=0xA9 && Property(scalar,GraphemeData.Pictographic)!=0,split=!first;
                if(!first) {
                    if(previous==1 && current==2)split=false;
                    else if(previous==1 || previous==2 || previous==3 || current==1 || current==2 || current==3)split=true;
                    else if(previous==9 && (current==9 || current==10 || current==12 || current==13))split=false;
                    else if((previous==12 || previous==10) && (current==10 || current==11))split=false;
                    else if((previous==13 || previous==11) && current==11)split=false;
                    else if(current==4 || current==5 || current==8 || previous==7)split=false;
                    else if(indic==1 && consonant && linker)split=false;
                    else if(previous==5 && pictographicZwj && pictographic)split=false;
                    else if(previous==6 && current==6 && regional%2==1)split=false;
                }
                if(first || split) {last=start;if(boundary!=null)boundary(start);}
                if(current==5) {pictographicZwj=pictographicRun;pictographicRun=false;}
                else if(current!=4) {pictographicRun=pictographic;pictographicZwj=false;}
                if(indic==1) {consonant=true;linker=false;}
                else if(indic==2 && consonant)linker=true;
                else if(indic!=3) {consonant=false;linker=false;}
                regional=current==6?regional+1:0;
                previous=current;first=false;
            }
            return last;
        }
        public static int LastStart(string text) {
            if(String.IsNullOrEmpty(text))return 0;
            int start=text.Length-1,scalar=text[start];
            if(start>0 && Char.IsLowSurrogate((char)scalar) && Char.IsHighSurrogate(text[start-1]))scalar=Char.ConvertToUtf32(text[--start],(char)scalar);
            if(start==0)return 0;
            int before=start-1,previous=text[before];
            if(before>0 && Char.IsLowSurrogate((char)previous) && Char.IsHighSurrogate(text[before-1]))previous=Char.ConvertToUtf32(text[--before],(char)previous);
            int current=Gcb(scalar),prior=Gcb(previous);
            // The common ASCII/CJK tail has no contextual grapheme rule.
            if(current==0 && prior!=7 && Property(scalar,GraphemeData.Conjunct)!=1 &&
                !(prior==5 && Property(scalar,GraphemeData.Pictographic)!=0))return start;
            return Scan(text,null);
        }
        public static string RemoveLast(string text) {return String.IsNullOrEmpty(text)?"":text.Substring(0,LastStart(text));}
    }
}
