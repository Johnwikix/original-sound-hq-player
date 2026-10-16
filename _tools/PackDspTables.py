"""从命名表目录重建紧凑 DSP 资源和 C# 索引，不依赖外部二进制文件。"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


def generate(root: Path, check: bool) -> None:
    catalog = json.loads((root / "Assets/dsp-tables.json").read_text(encoding="utf-8"))
    if catalog["formatVersion"] != 1:
        raise ValueError("不支持的系数目录版本")
    offsets = {}
    sizes = {}
    data = bytearray()
    for table in sorted(catalog["tables"], key=lambda entry: entry["name"]):
        name = table["name"]
        if name in offsets:
            raise ValueError(f"重复的系数表：{name}")
        data.extend(b"\0" * (-len(data) % 8))
        offsets[name] = len(data)
        payload = bytes.fromhex(table["data"])
        sizes[name] = len(payload)
        data.extend(payload)
    for table in catalog["tables"]:
        for reference in table["references"]:
            position = reference["offset"]
            target = reference["table"]
            within = reference["within"]
            if position < 0 or position + 4 > sizes[table["name"]] or not 0 <= within < sizes[target]:
                raise ValueError("系数表引用越界")
            struct.pack_into("<I", data, offsets[table["name"]] + position, (1 << 31) | (offsets[target] + within))
    namespace = re.search(r"namespace ([\w.]+);", (root / "CoefficientStore.cs").read_text(encoding="utf-8"))[1]
    source = f"namespace {namespace};\n\n/// <summary>按用途索引紧凑系数资源；位置仅属于本项目的数据格式。</summary>\ninternal static class CoefficientTables\n{{\n"
    names = set()
    for symbol in sorted(catalog["symbols"], key=lambda entry: entry["name"]):
        if symbol["name"] in names or not 0 <= symbol["within"] < sizes[symbol["table"]]:
            raise ValueError("系数符号重复或越界")
        names.add(symbol["name"])
        position = offsets[symbol["table"]] + symbol["within"]
        source += f"    internal const uint {symbol['name']} = CoefficientStore.ResourceFlag | {position}u;\n"
    source += "}\n"
    store = (root / "CoefficientStore.cs").read_text(encoding="utf-8")
    store = re.sub(r"private const int ByteLength = \d+;", f"private const int ByteLength = {len(data)};", store)
    digest = hashlib.sha256(data).hexdigest()
    store = re.sub(r'private const string Digest = "[a-f0-9]+";', f'private const string Digest = "{digest}";', store)
    outputs = {
        root / "Assets/dsp-tables.bin": bytes(data),
        root / "CoefficientTables.cs": source.replace("\n", "\r\n").encode("utf-8"),
        root / "CoefficientStore.cs": store.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"),
    }
    for path, payload in outputs.items():
        if check:
            if path.read_bytes() != payload:
                raise ValueError(f"生成结果与文件不一致：{path}")
        else:
            path.write_bytes(payload)
    print(f"{'校验' if check else '生成'}通过：{len(offsets)} 个系数表，{len(data)} 字节，SHA-256 {digest}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="包含 CoefficientStore.cs 的系数目录")
    parser.add_argument("--check", action="store_true", help="仅检查生成结果，不写入文件")
    options = parser.parse_args()
    generate(options.root.resolve(), options.check)
