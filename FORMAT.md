# Quire 文件格式 6

包结构中的整数使用小端序；偏移、总长度、块数量、记录数量采用非负 Int64，单独注明的计数字段除外。密码学标量、坐标及签名的整数编码在下文单独规定。禁止直接序列化托管结构体。Magic 为 8 字节 ASCII `PK.QUIRE`，文件默认后缀为 `.asset`，构建时可以自定义后缀。读取器只接受格式 6，不兼容旧格式。

每次构建产生独立的 128 位 `TagUid`，每个实际输出包具有独立的 128 位 `PackageUid`。原有 UInt32 `TagId` 保留为 CLI 内部的 8 位小写十六进制 ID，并继续用于加密 AAD；它不作为包清单中的全局身份。Guid 的 16 字节编码使用 .NET `Guid.TryWriteBytes()` / `new Guid(span)` 顺序：前面的 4、2、2 字节字段分别为小端，最后 8 字节保持顺序。

## 文件头（4096 字节）

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 8 | ASCII `PK.QUIRE` |
| 8 | 4 | Int32 版本，固定为 6 |
| 12 | 4 | Flags：bit0=内容加密，bit1=签名；允许 0（公开未签名）、2（公开签名）、3（加密签名），其余非法 |
| 16 | 4 | UInt32 内部 TagId |
| 20 | 4 | Int32 ManifestLength，包清单编码长度 |
| 24 | 4 | Int32 TagName 的 UTF-8 字节数，1–255 |
| 28 | 4 | 保留，必须为 0 |
| 32 | 8 | 构建时间，UTC DateTime ticks |
| 40 | 4 | 块大小，64 KiB–1 MiB 的二次幂 |
| 44 | 4 | KeyVersion，未签名为 0，签名固定为 2 |
| 48 | 8 | 完整文件长度，必须等于实际长度 |
| 56 | 8 | 目录索引记录数量 |
| 64 | 8 | 目录区原始长度 |
| 72 | 8 | Metadata 区原始长度 |
| 80 | 8 | 块表原始长度 |
| 88 | 8 | 内容区起始偏移 |
| 96 | 16 | 每个物理包独立的随机盐 |
| 112 | 16 | PackageUid，不能为全零 Guid |
| 128 | 16 | TagUid，不能为全零 Guid |
| 144 | 32 | 包清单完整编码的 SHA-256 |
| 176 | TagName 字节数 | 严格 UTF-8 编码的用户 Tag 名称，不含终止符 |
| TagName 末尾 | 至 432 | 保留，必须为 0 |
| 432 | 8 | AuthenticationOffset，内容区结束及摘要表起点；未签名为 0 |
| 440 | 8 | AuthenticationCount，32 字节摘要项数量；未签名为 0 |
| 448 | 32 | AuthenticationHash，完整摘要表的 SHA-256；未签名全零 |
| 480 | 64 | ECDSA P-256.SignHash(SHA256(前 480 字节))，IEEE P1363 编码；未签名全零 |
| 544 | 至 4032 | 保留，必须为 0 |
| 4032 | 32 | HMAC-SHA256(masterKey, 前 4032 字节) |
| 4064 | 32 | SHA-256(前 4064 字节) |

TagName 不得为空白或含控制字符。文件头完整性摘要允许在获取客户端密钥前检查损坏。签名包首先使用外部可信 P-256 公钥验证前 480 字节的 SHA-256 签名，绑定 Tag、包身份、清单及认证摘要表；之后验证 HMAC。签名是 IEEE P1363 固定字段拼接 `r || s`，r、s 各为 32 字节大端整数。签名与验证直接使用已计算的 32 字节摘要，不再对摘要执行一次 SHA-256。AES 密钥泄露不能伪造签名。

## 本次 Tag 的包清单

包清单紧接文件头，从绝对偏移 4096 开始，长度恰为 ManifestLength。清单不加密，其 SHA-256 受文件头 HMAC 以及适用的 ECDSA P-256 签名保护。

| 字段 | 编码 |
|---|---|
| 包数量 | Int32，1–65536 |
| 每项 PackageUid | 16 字节 Guid，非零且不得重复 |
| 每项文件名字节数 | UInt16，1–255 |
| 每项构建文件名 | 指定字节数的严格 UTF-8，不含终止符或目录路径 |

清单至少 23 字节，最多 `4 + 65536 × (16 + 2 + 255)` 字节。禁止尾随字节、非法 UTF-8、非规范文件名、重复 UID，且每个包携带的清单必须包含自己的 PackageUid。

同一次构建的全部输出携带相同的 TagUid、TagName、构建时间和清单编码。清单只包含这次构建实际产出的包：A 的清单只列 A 的输出，B 的清单只列 B 的输出；不包含父 Tag、历史包或未变化而没有输出的包。没有任何输出时，不额外生成标记包。

清单中的文件名仅用于缺失提示。完整性匹配使用 PackageUid，物理文件重命名不改变身份。每次打开包都验证头部认证、清单摘要及结构；开启 `VerifyPackageChain` 时，还检查扫描范围中每个已发现 Tag 的清单成员是否全部存在，并拒绝重复的 PackageUid。该检查不要求历史 Tag 存在，也无法识别一个完全未被发现的 Tag。

同 Tag 的清单可只解码和保留一次；其他物理包仍须校验各自清单字节的 SHA-256 并确认自己的 UID 属于共享清单。不能仅凭头部声明相同摘要而跳过物理清单校验。

## 受保护的前置区域

目录、Metadata、块表依次紧邻包清单。各区按 65536 字节独立保护，每页保存密文及其后 16 字节 GCM tag；尾页按实际长度保存，零长度区没有页。

`ProtectedLength(L) = L + ceil(L / 65536) × 16`，加法和乘法必须检查溢出。

- 目录偏移：`4096 + ManifestLength`。
- Metadata 偏移：目录偏移加目录的 ProtectedLength。
- 块表偏移：Metadata 偏移加 Metadata 的 ProtectedLength。
- 内容区起点：块表偏移加块表的 ProtectedLength，再向上对齐到 8 字节。

受保护页面的位置由原始页号直接计算，不需要把整个区域载入内存。运行库可以常驻目录，也可以按需读取相同布局。

## 目录记录（128 字节）

目录原始数据先存放全部定长记录，随后紧密拼接各记录的 UTF-8 路径。记录按路径 UTF-8 字节序严格递增。

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 8 | 路径在目录原始数据中的偏移 |
| 8 | 4 | 路径 UTF-8 长度，1–4096 |
| 12 | 1 | Kind：0=完整资源，1=仅 Metadata，2=删除记录 |
| 13 | 1 | bit0=有 Metadata，bit1=内容分块；其他位必须为 0 |
| 14 | 2 | 保留，必须为 0 |
| 16 | 8 | 内容原始长度 |
| 24 | 8 | 连续内容绝对偏移；分块内容为 0 |
| 32 | 8 | 内容实际存储长度，分块时为各块之和 |
| 40 | 8 | Metadata 在其原始区域中的偏移 |
| 48 | 4 | Metadata 编码长度，上限 1 MiB |
| 52 | 4 | 保留，必须为 0 |
| 56 | 8 | 该资源的首块全包序号 |
| 64 | 8 | 块数量 |
| 72 | 32 | 原始完整内容的 SHA-256 |
| 104 | 24 | 保留，必须为 0 |

仅 Metadata 记录必须带有 Metadata，其内容字段及摘要全部为零。删除记录仅保留路径，Metadata 标记、分块标记、内容字段、Metadata 字段及摘要全部为零；加载后用于遮蔽旧索引中的路径，不修改旧包的物理内容。

完整的空资源仍保存空序列的 SHA-256。没有 Metadata 时，其偏移和长度均为零；空 Metadata 是有效的 8 字节对象。路径区必须紧密覆盖，禁止重复键、反斜杠、空段、`.`、`..` 和非法路径字符。

## 块描述符（32 字节）

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 8 | 数据块绝对文件偏移 |
| 8 | 4 | 存储字节数，包含可选的 16 字节认证标签 |
| 12 | 4 | 原始字节数 |
| 16 | 8 | 全包唯一块序号，等于块表记录位置 |
| 24 | 1 | Codec：0=原始块，1=LZ4，2=zlib |
| 25 | 7 | 保留，必须为 0 |

压缩、加密或签名资源的非空内容均使用块表；只有未压缩、未加密、未签名资源可以保存连续原始字节。块数量由原始长度和块大小决定，最后一块可短于块大小。块可在内容区交错存储，通过块表直接定位。

压缩算法与压缩策略相互独立：`Always` 使用所选算法保存压缩块；`Adaptive` 仅在压缩结果小于原始块时保留压缩，否则写入 Codec=0。格式 6 没有改变这些压缩语义。读取器拒绝未知的 Codec。

## Metadata

头为 ASCII `AMTA`（4 字节）和字段数 Int32（4 字节）。之后是每字段 24 字节描述符，再是紧密拼接、按 UTF-8 排序的键名，最后是按字段顺序排列、起点 8 字节对齐的值。

描述符：keyOffset:Int32、keyLength:Int32、valueOffset:Int32、valueLength:Int32、elementCount:Int32、type:Byte、reserved:3Bytes。偏移相对 Metadata 对象开始。没有末尾填充；所有键、值的位置必须符合规范布局。

类型 ID：1=string、2=int64、3=double、4=bool、5=string[]、6=int64[]、7=double[]、8=bool[]。标量 elementCount=1。字符串为严格 UTF-8；整数和 double 各 8 字节；bool 只允许字节 0 或 1。数值和 bool 数组连续存储。字符串数组保存 N+1 个 Int32 相对值区偏移，后接 UTF-8 字节；第一个偏移等于偏移表长度，最后一个等于值区长度。禁止非有限 double。

## 密钥、nonce 和 AAD

CLI 使用 YAML 的 `secret-key: KEYNAME` 选择当前用户的全局命名密钥，生成加密并签名的包（Flags=3）；`secret-key: none` 或省略时生成未加密、未签名的包（Flags=0）。密钥名称只用于本机查找，不写入包，也不参与密码派生。格式仍允许公开签名包（Flags=2）。

固定密码派生 profile v2（与包格式版本独立）：严格 UTF-8 密码，不 trim、不做 Unicode 归一化；PBKDF2-SHA256，盐为 ASCII `Quire/password-keys/v2`，迭代 600000 次，输出 32 字节根密钥。

从根密钥分别进行 HKDF-SHA256，salt=empty、outputLength=32：info=`Quire/encryption/v2` 生成 AES 主密钥，info=`Quire/signing/v2` 生成 P-256 私钥候选 D。将 D 视为 32 字节大端无符号整数，要求 `0 < D < n`，其中曲线阶 `n = FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551`。如果候选无效，使用同一根密钥和 HKDF 参数，以 `ASCII("Quire/signing/v2/retry/") || UInt32BE(counter)` 为 info，counter 从 1 起递增，直到候选有效；不使用取模缩减。

公钥 Q 由 .NET 原生密码学实现根据标准 P-256 曲线计算 `Q = D × G`，不自行实现曲线运算。发布私密材料为 `AES 主密钥 || D`，各 32 字节，共 64 字节。客户端材料为 `AES 主密钥 || Q.X || Q.Y`，各 32 字节，共 96 字节；坐标采用大端无符号整数编码，前导零必须保留。两条派生分支独立，根密钥和 D 不可发给客户端。`AssetKey` 的字符串形式为客户端材料的标准 Base64 编码，恰好 128 个字符，无填充。

相同密码确定性重建的是密钥，ECDSA 签名值不要求确定性。v2 派生结果和客户端格式与旧版不兼容。PBKDF2 不具备内存困难型派生算法的抗并行猜测成本，客户端材料可以用于离线验证密码猜测，仍须使用高熵密码。

每包加密 masterKey：`HKDF-SHA256(clientAES, salt=header.Salt, info=ASCII("Quire/package-encryption/v1") || PackageUidBytes, outputLength=32)`。PackageUidBytes 使用前述 .NET Guid 编码。每包盐为独立随机 16 字节；包盐不参与密码派生，因此不改变客户端身份。

公开保护 masterKey：`HMAC-SHA256(UTF8("Quire public index protection v1"), salt)`。派生标签中的 `v1` 字面量保持不变，不表示文件格式版本。公开密钥可以自行推导，公开保护本身不提供保密性或发布者身份认证；公开签名包另外通过 ECDSA P-256 认证来源。

域：1=目录，2=Metadata，3=块表，4=资源内容。前三个域始终使用 GCM；域 4 仅在内容加密时使用。目录、Metadata 和块表的序号为区域内页号，内容的序号为全包块号。

每 1024 个序号更换一个子密钥：`HKDF-SHA256(masterKey, outputLength=32, salt=empty, info)`。info 为 16 字节：domain:Int32、reserved:Int32=0、group:Int64，其中 group=sequence/1024。块大小为最大 1 MiB 时，每个子密钥处理不超过 1 GiB 原始数据。

nonce 为 12 字节：domain:Int32 + sequence:Int64。

AAD 为 40 字节：TagId:UInt32、reserved:12Bytes=0、domain:Int32、sequence:Int64、rawLength:Int32、codec:Byte、reserved:3Bytes=0、encodedPlainLength:Int32。这里继续使用内部 UInt32 TagId；TagUid、PackageUid 和清单摘要由文件头 HMAC 认证。

每个包的随机盐必须重新生成，不允许在相同主密钥和序号下重写不同明文。AAD 不绑定物理偏移，允许 CLI 先并行编码再分配最终位置。路径和资源描述受目录页认证保护。

## 认证摘要表

签名包的内容区之后紧密附加 SHA-256 摘要表，无额外表头，每项 32 字节。按以下顺序排列：目录编码页、Metadata 编码页、块表编码页、资源编码块。页摘要包括其 GCM tag，资源摘要覆盖实际存储的全部字节（包含适用的压缩数据及 GCM tag）。每个区域内部按页号或全包块序号排列，位置本身确定身份。

`AuthenticationCount = ceil(DirectoryLength/65536) + ceil(MetadataLength/65536) + ceil(BlocksLength/65536) + BlocksLength/32`。表长为 Count×32，必须满足 `AuthenticationOffset + Count×32 = FileLength`；摘要表不能与内容区域重叠。签名包的数据范围均以 AuthenticationOffset 为上界，不能指向摘要表。未签名包没有摘要表。

Open 验证包头签名后，读取摘要表并与 AuthenticationHash 比较，保留经过认证的连续摘要数组。后续访问实际页/块时先检查 SHA-256，再解密、解压或交付数据；只有通过认证的页面才允许进入缓存。无需为每块执行 ECDSA 验签，也不需要打开时预读所有资源内容。摘要表常驻内存长度为 Count×32，当前上限为单个托管字节数组容量，独立于页缓存预算。

提供 `ReadOptions.Key` 或 `KeyProvider` 表示要求整个集合签名，拒绝未签名降级。客户端公钥必须由外部可信配置提供，不能接受包自行指定的新公钥。关闭整文件内容哈希不影响此摘要认证；范围读取、流式读取和 Meta 查询同样验证实际访问的页/块。
## 发布与读取

构建写入临时文件，回填包清单、受保护区域及认证摘要表，最后签发文件头，Flush 后关闭并重命名。包加载后视为不可变，不支持原地修改内容或 Metadata。

文件内容的 SHA-256 校验独立于包清单完整性检查。完整 `Read` / `ReadAsync` 默认校验内容 SHA-256，可以通过读取选项或单次调用参数按文件控制；`Verify` / `VerifyAsync` 执行完整内容校验。流式和局部读取检查访问到的块结构、签名摘要及适用的 GCM 认证，不据此声明已校验完整文件内容。
