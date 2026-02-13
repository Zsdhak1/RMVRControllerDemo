using UnityEngine;
using UnityEngine.UI;

public class SmoothHealthBar : MonoBehaviour
{
    [Header("UI引用")]
    public Slider healthSlider; // 前景血条（当前血量）
    public Slider easeSlider;   // 背景缓冲血条（可选，如果不想要黄色缓冲层，可以忽略）

    [Header("数值设置")]
    public float maxHealth = 100f;
    private float currentHealth;
    
    [Header("动画设置")]
    public float lerpSpeed = 0.05f; // 插值速度，越小越慢

    void Start()
    {
        // 如果没有外部初始化，默认满血
        if (currentHealth == 0) currentHealth = maxHealth;
        
        // 初始化 Slider
        if(healthSlider != null) 
        {
            healthSlider.maxValue = maxHealth;
            healthSlider.value = currentHealth;
        }
        
        if(easeSlider != null)
        {
            easeSlider.maxValue = maxHealth;
            easeSlider.value = currentHealth;
        }
    }

    void Update()
    {
        // 核心逻辑：使用 Lerp 插值让 Slider 的值平滑靠近目标值
        if (healthSlider != null && Mathf.Abs(healthSlider.value - currentHealth) > 0.01f)
        {
            healthSlider.value = Mathf.Lerp(healthSlider.value, currentHealth, lerpSpeed);
        }

        // 缓冲层逻辑（可选）
        if (easeSlider != null && Mathf.Abs(easeSlider.value - currentHealth) > 0.01f)
        {
            easeSlider.value = Mathf.Lerp(easeSlider.value, currentHealth, lerpSpeed * 0.5f);
        }
    }

    // 设置当前血量（由 UIManager 调用）
    public void SetHealth(float targetHealth)
    {
        currentHealth = targetHealth;
        // 限制数值不低于0
        if (currentHealth < 0) currentHealth = 0;
        if (currentHealth > maxHealth) currentHealth = maxHealth;
    }
}