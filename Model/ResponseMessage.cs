using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model
{
    public class ResponseMessage
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } // 响应/错误信息

        [JsonPropertyName("result")]
        public string Result { get; set; } // 操作结果数据
    }
}
