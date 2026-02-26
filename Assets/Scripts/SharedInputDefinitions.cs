using UnityEngine;

// [System.Flags] 特性允许在 Unity 的 Inspector 面板里像勾选多项框一样同时选中多个按键
[System.Flags]
public enum KeyboardBitMask : uint
{
    None = 0,
    W_Key = 1 << 0,
    S_Key = 1 << 1,
    A_Key = 1 << 2,
    D_Key = 1 << 3,
    Shift_Key = 1 << 4,
    Ctrl_Key = 1 << 5,
    Q_Key = 1 << 6,
    E_Key = 1 << 7,
    R_Key = 1 << 8,
    F_Key = 1 << 9,
    G_Key = 1 << 10,
    Z_Key = 1 << 11,
    X_Key = 1 << 12,
    C_Key = 1 << 13,
    V_Key = 1 << 14,
    B_Key = 1 << 15
}