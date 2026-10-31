namespace TypeTrail {
    internal static class KeyboardInputPolicy {
        public static bool IsPaste(int key, bool ctrl, bool alt, bool win, bool shift) {
            return !alt && !win && ((ctrl && key == 86) || (!ctrl && shift && key == 45));
        }
        public static bool Modifies(int key, bool ctrl, bool alt, bool win, bool shift, bool altGr) {
            if (win || (alt && !altGr)) return false;
            if (IsPaste(key, ctrl, alt, win, shift)) return true;
            if (ctrl && !altGr) return key == 86 || key == 88 || key == 90 || key == 89 || key == 8 || key == 46;
            return key == 8 || key == 9 || key == 13 || key == 32 || key == 46 ||
                (key >= 48 && key <= 90) || (key >= 96 && key <= 111) || key >= 186;
        }
        public static bool InvalidatesPaste(int key, bool ctrl, bool alt, bool win, bool shift) {
            if (IsPaste(key, ctrl, alt, win, shift)) return false;
            return key == 8 || key == 9 || key == 13 || key == 27 || key == 46 ||
                (key >= 33 && key <= 40) || ctrl || alt || win;
        }
    }
}
