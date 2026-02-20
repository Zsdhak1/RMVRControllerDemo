using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class TeammateItem : MonoBehaviour
{
    public TextMeshProUGUI idText;    // 显示 "R3" 或 "B4"
    public Slider hpSlider;           // 血条
    public Image fillImage;           // 血条颜色
    public TextMeshProUGUI statusText;// 显示 "存活" 或 "阵亡"

    public void UpdateInfo(int id, int hp, int maxHp)
    {
        // 1. ID 显示
        idText.text = id.ToString(); // 或者优化为 "工程", "步兵" 等

        // 2. 血条
        if (maxHp > 0)
        {
            float ratio = (float)hp / maxHp;
            hpSlider.value = ratio;
            // 颜色：大于50%绿色，小于20%红色，中间黄色
            if (ratio > 0.5f) fillImage.color = Color.green;
            else if (ratio > 0.2f) fillImage.color = Color.yellow;
            else fillImage.color = Color.red;
        }

        // 3. 状态文本 (灰色显示阵亡)
        if (hp <= 0)
        {
            statusText.text = "阵亡";
            statusText.color = Color.gray;
            fillImage.color = Color.gray;
        }
        else
        {
            statusText.text = $"{hp}"; // 显示具体血量数值
            statusText.color = Color.white;
        }
    }
}