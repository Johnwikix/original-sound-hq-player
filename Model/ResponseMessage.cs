using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model
{
    public class ResponseMessage
    {
        // 0 表示失败，1 表示成功，10表示返回指令
        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } // 响应/错误信息

        [JsonPropertyName("result")]
        public string Result { get; set; } // 操作结果数据
    }
}
