> 当前交付摘要见 [最终交付记录](FINAL_DELIVERY_20260916.md)。本文保留历史过程；旧版本号和旧阻断不代表当前状态。下载维护与真实测试已由用户接手。

# 跨端剩余工作完成跟踪（2026-09-16）

此表继承 CROSS_END_REVIEW_PLAN_20260916.md 与本任务最近一次剩余项答复的完整范围，不用历史初始勾选代替当前证据。整项目标仍在进行中。

|项目|当前状态|关闭条件|
|---|---|---|
|S03 请求大小保护|已实施并上线；213/213测试及10项线上检查通过|真实Worker受限读取、413/400、正常请求兼容、配置不变|
|S02 代理限流归属|已确认共享桶并修复上线；签名代理及实际计数器验证通过|可信客户端身份设计、伪造头回归、实际代理链路证据|
|G02 术语差异预览与选择合并|双端已实施并交付；账号/语言切换及冲突测试通过，生产往返待授权验收|APP/网页冲突预览、选择合并、账号/语言切换安全、最新状态不保留旧快照|
|H02 历史元数据|已实施上线；新记录语言/起止时间、旧记录兼容及计费不变回归通过|新记录语言及准确时间、旧记录兼容、不改变计费事务语义|
|A02 任务恢复边界|损坏记录保护及12阶段恢复回归已交付；其他压力边界仍待补齐|断网/中断/磁盘满/损坏/账号切换/输出冲突测试及必要修复|
|P02 支付状态恢复边界|本地修复、隔离回归及APP交付完成；真实支付验收仍待授权|持久文件损坏/写失败/关闭重开/账号切换测试及必要修复|
|R01 发行清单一致性|本地候选清单与包内一致性验证完成；公开版本/下载对齐待授权|平台/版本/校验信息统一；公开发布需另行授权|
|S01 会话与安全策略|CSP与网页凭证过期边界已上线验证；APP/Worker凭证生命周期及全登录态审查未完|XSS/CSP/凭证生命周期威胁矩阵、必要修复及验证|
|W01 全站回归|已有47+9用例，仍需扩展|主要页面、错误状态、键盘/设备交互验证|
|生产云同步往返|尚未验收|授权账号APP→网页→APP验证，不以匿名健康代替|
|CAD真实完整链路|本机浩辰2024自动加载已通过；其他部分待验收|受控图纸调度/翻译/回写、实体类型及其他受支持版本|
|真实支付链路|待授权验收|真实回调/权益/对账/补偿及调度观察，不擅自扣费|
|真实邮件链路|待授权验收|送达与密码重置流程；不擅自发送邮件|

## 本轮实施边界

- 新增 cf-worker/src/request-body.ts，在入口限量读取原始字节；不改变翻译、计费、CAD算法。
- 术语及翻译16MiB（覆盖1000条极限转义Unicode词库），普通接口64KiB，反馈12000字节、用户管理4096字节，沿用已有专用接口较低上限。
- 不信任Content-Length，实际流超限即取消；合法JSON内容不重新序列化；支付回调保留原始签名字节与现有解析规则。
- 新增11项边界测试，全套213/213；类型检查通过。证据 artifacts/cross-end-completion-20260916。
- 线上bundle与本地候选对比，差异仅新增入口保护模块和入口调用；未夹带已有脏工作区的其他改动。
- 已只读验证51个数据库结构对象一致，无需迁移；候选使用线上原有变量与数据库绑定，密钥不变。
- 本轮未构建APP、未替换release、未公开下载、未做生产账号写入和真实扣费。

## 请求入口保护上线验收

- Worker版本：2997b0f0-fe46-41b2-9c2d-4dc821d01d9e，100%流量已切换。使用版本发布保留域名、路由及调度触发器。
- 自定义API域名与官网同源代理分别验证健康200、匿名词库401、错误JSON400、超大请求413、预检204，共10/10通过。没有登录/写账号/发邮件/扣费。
- 上线后30个绑定（含密钥名称和非敏感配置值）、兼容设置、日志/placement/tail设置均与上线前一致；远端代码与已审查候选逐字匹配。
- 证据：artifacts/cross-end-completion-20260916/live-verification.json、worker-tests.log、typecheck.log、bundle-diff.txt、deploy.log。
- 其余项目保持未关闭，下一步推进代理限流归属核实与同步冲突合并。

## S02 代理限流归属修复与验收（2026-09-16）

- 原生产Pages代理到workers.dev时，实际D1反馈限流键对应Cloudflare公共跨zone地址2a06:98c0:3600::103，网页用户存在共享IP桶问题。只读观察计数器、发送格式无效反馈，不创建反馈、账号、订单、邮件。
- 中间方案仅改自定义域名与x-real-ip仍失败；真实计数器证明Pages仍是跨zone子请求。该方案不作为完成证据。
- 最终使用独立WEB_PROXY_IDENTITY_KEY：Pages仅签名边缘提供的客户端地址、时间、方法、完整路径及查询、Authorization；Worker校验HMAC与60秒有效期，失败回退边缘地址。账号限流不变。用户提交的身份签名与转发头不直接转发。身份签名只用于限流，不授予认证权限。
- 新密钥只增加到Worker及Pages production secret，不复用支付/管理员密钥，不修改预览配置；本地临时密钥文件已删除。未改翻译、计费事务、CAD核心。
- 最终Worker：21bde959-da05-4a9c-b478-d419d377bbcf；Pages：e3099ba3-66f2-49a8-9e8f-fefd862abd43。前一Pages 79e41b66方案被替代。
- Worker完整220/220、类型检查通过；网页基础18/18；实际Pages签名器与Worker校验器联测通过，含IPv4/IPv6、伪造签名/地址/时间/方法/路径/Authorization、账号独立限流。
- 真实线上同一出口的APP直连路径与网页代理请求计入同一个用户桶；公共桶不增长；伪造XFF/x-real-ip/自定义签名未产生伪造IP桶。伪造CF-Connecting-IP曾由边缘拒绝403。不同真实出口尚未人工验收，隔离测试证明不同已签名地址分桶。
- 最终8项API/代理健康与安全响应检查通过，60个静态文件仍与原生产候选完全一致；网站仅Functions改变，未夹带工作区界面改动。
- Worker既有30个绑定及运行设置不变，仅增加签名密钥；Pages原配置不变，仅production增加签名密钥。没有数据库迁移，无APP构建/release替换。
- 证据目录：artifacts/cross-end-completion-20260916/proxy-attribution，包含final-verification.json、attribution-before.json、attribution-after.json、worker-tests.log、web-tests.log、worker-bundle-diff.txt、发布日志。计数器哈希及配置快照不公开。
- 剩余G02/H02/A02/P02/R01/S01/W01及真实验收继续保留完整范围，不宣称整体完成。
## G02 网页选择合并进展（2026-09-16）

- 网页新增三方逐条比较和本地/云端选择，冲突必须显式选择；取消保留草稿。只保留内存比较依据，不新增旧快照持久化。
- 合并前读取最新云端版本，保存携带对应版本；预览后再次被其他设备修改仍安全拒绝，不盲目覆盖。账号切换关闭预览并阻止保存。
- 网页测试29/29、隔离候选浏览器联测14/14通过，覆盖独立修改、删除冲突、丢失响应、账号切换、再次冲突、转义显示及键盘取消。
- Pages部署成功：58724f2f（部署地址标识）。仅4个静态文件发生变化，签名代理保持原样；生产自定义域名62个静态文件逐一校验与候选一致。
- APP合并预览尚未实施；未进行生产账号术语写入/往返验收；此次无APP构建及release替换。不能据此关闭整个G02。
- 证据：artifacts/cross-end-completion-20260916/glossary-merge，含pages-deploy.log、static-after.json、scoped-files.json、candidate-browser.log、web-tests.log及移动端预览。

## G02 APP选择合并与本地交付（2026-09-16 12:07）

- 云端同步菜单新增“比较并合并（推荐）”，保留明确的本机替换云端/下载操作；不改翻译、计费和CAD核心。
- 独立CloudGlossaryMerge三方比较模块，稳定ID优先匹配，无共同依据时不把云端独有条目当成删除；冲突显式选择，恢复云端已删除条目移除旧ID。禁止歧义、重复结果及超过1000条的合并。
- 独立APP预览窗口显示本机/最新云端内容及备注、分类、文件夹、启停状态，取消保留草稿；账号/语言/草稿上下文变化阻止保存。只有明确确认后携带预览版本提交，409不覆盖；保留本机来源、使用次数和最近使用时间。
- 不新增旧快照文件，仅使用有界内存比较依据；云端成功但本机保存失败时保留合并草稿并明确提示，不宣称全部成功。
- 新增12项比较单测；完整Core176/176，安装器49项、CAD部署16/16、布局回归和APP UI smoke均通过。UI用隔离服务实际驱动预览，覆盖未选择阻止保存、取消、再次冲突、版本、选择合并、本机专有元数据、独立修改和账号切换自动关闭。已检查截图布局。
- 完整Publish-Desktop（无BuildOnly/SkipTests）交付到D:\DWGC2E\release；版本2.1.1+ui.20260916-120540.a3f6f1f，SHA256 D2540F60DB60F26203347FC45446212A1F07D5DD6BBD4124C4402E161716CC14。安装文件哈希及原有3个便携数据文件逐项校验通过。清理流程仅保留最新候选/安装包与一份上一版本回滚备份。
- 证据目录artifacts/cross-end-completion-20260916/app-glossary-merge：publish.log、portable-before.json、delivery-verification.json及预览/完成截图。
- G02仍需补充语言切换专项UI验收及真实授权账号的跨端往返；H02/A02/P02/R01/S01/W01与CAD、真实支付/邮件验收范围保持不变。此次没有生产词库写入、公开下载发布、真实扣费或发送邮件。


## H02 历史元数据实施与上线（2026-09-16）

- 为translation_requests增加source_language、target_language、started_at、completed_at四个可空字段。新请求在原预留/结算语句中附带元数据，不增加计费事务，不改变字符计算、幂等键、额度/退款触发器或上游调用规则。
- 正常完成记录实际服务端起止时间，重放保留原时间。旧记录不补造语言或准确时间，继续显示“约”估算时间；缺失起止字段返回null。超时清理记录明确标记expiry_deadline，网页显示“超时截止”，不将之后的清理时刻冒充真实完成时刻。
- 历史页面新增结束时间显示与搜索，保留旧记录、分页、账号隔离、失败/部分成功和无下载的原有语义。仅上线history.html及js/history.js两文件，未夹带工作区其他页面变更；原代理Functions保持不变。
- Worker226/226及类型检查通过，其中新增6项实际路由/SQLite/迁移回归；另扩展真实workerd/D1测试覆盖新增字段和到期SQL。浏览器联测16/16、网页29/29通过，已检查移动端截图。
- 发布前只读核实当前Worker/Pages部署和31个绑定、53个数据库对象；只执行0010新增列迁移，远端旧迁移清单显示0008/0009未登记但相关结构已存在，未重放无关迁移。0010执行后登记到d1_migrations。
- 迁移后核实原10列完全一致，只增加4个可空列；其他表、索引、触发器逐一保持不变。无用户数据删除或改写、无凭证轮换。
- Worker部署版本66a75395-78e7-4373-873c-83d54ac6fd48，100%流量；Pages部署1670cfae-3bc7-4e08-8a02-a6febc10641b。线上Worker代码与候选一致，既有绑定/配置与Pages生产及预览配置不变。自定义域名和同源代理10/10匿名健康/安全检查通过，62个静态文件逐一与候选一致。
- 证据artifacts/cross-end-completion-20260916/history-metadata：worker-tests.log、typecheck.log、browser-tests.log、web-tests.log、schema-before/after.json、bundle-diff.txt、migration.log、worker-upload/deploy.log、pages-deploy.log、final-verification.json、static-after.json及history-mobile.png。配置私有快照不公开。
- 未调用真实翻译上游、未生产登录/写入测试账号、未发送邮件或扣款。本轮无APP构建及release替换。真实用户历史验收不以匿名健康检查代替；G02专项验收及A02/P02/R01/S01/W01和其余真实链路继续保留。

## P02 支付恢复边界与本地交付（2026-09-16 12:37）

- 修复首次持久化失败后内存意图仍存在、下一次点击可能绕过保存直接请求下单的问题：每次Checkout之前都必须先成功保存原幂等标识。
- 恢复文件使用独立临时文件、Flush(true)及原子替换；写入失败保留原记录并清理本次临时文件，提示不要重复购买。损坏JSON、null根值、null/非法Key阻止购买且保留原文件。
- 已付款记录清理失败时恢复内存中的原意图，避免内存与磁盘状态分离；解除占用后可再次确认并清理。
- 新增BillingPersistenceSmoke隔离验收：损坏记录、同账号双窗口、连续写失败不请求Checkout、恢复后沿用原Key、关窗重开、服务器响应后的保存失败、网络响应丢失、已支付文件清理失败重试、账号失效关闭与不同账号文件隔离。全部模拟订单，不调用真实支付。
- 首次运行发现测试把JSON格式差异误当成数据丢失；改为在模拟服务器响应前记录实际已落盘字节，再验证失败后字节完全不变。随后完整发布门禁通过：Core176/176、布局21/21、安装器49项、CAD16/16、APP UI smoke通过。
- 正常Publish-Desktop已替换D:\DWGC2E\release，版本2.1.1+ui.20260916-123614.a3f6f1f，SHA256 D3947266F4781B618CB770F0F841C68B37A478341BBA5F3A15371C9B8758FD97。安装哈希匹配，原有3份便携数据逐项不变，回滚备份仅1份。
- 证据artifacts/cross-end-completion-20260916/payment-recovery：publish.log、portable-before.json、delivery-verification.json及隔离订单截图。截图中的历史列表DisplayState为模拟数据默认值，不作为真实支付到账证据。
- 本轮未改Worker计费/结算、翻译及CAD核心，未发布公开下载、真实扣款或邮件。磁盘占用与写入失败测试不等同于实际磁盘耗尽测试；真实支付回调/权益/对账/补偿仍待授权验收。其余A02/R01/S01/W01、G02语言专项和真实跨端/CAD链路仍未关闭。
## A02 损坏记录保护与恢复阶段回归（2026-09-16 12:42）

- 修复JsonTaskStore读取损坏文件后按空队列启动、后续保存可能覆盖唯一原始恢复信息的问题。只有遇到无法完整读取的记录，首次后续保存或清除前才复制原始字节到独立recovery文件；无法保存原始证据时阻止替换，继续报告保存失败，不影响翻译运行。正常队列不创建日常旧快照。
- 损坏JSON、空文件、null文档、缺路径/空条目均保留原始记录；之后正常保存不重复备份同一份证据。此类异常恢复证据是数据安全例外，不纳入普通构建垃圾删除。当前提示在日志及现有保存失败反馈中，恢复文件的专用UI入口仍未实施。
- 新增19项Core用例：4种损坏输入、显式清除保护、12种任务状态恢复、备份失败禁止覆盖及解除锁后恢复、正常空队列不产生快照。覆盖中断状态恢复Pending而非自动执行、终态保持原状、成功译文/检查点/输出路径/任务ID不丢失。
- 完整发布门禁Core195/195、安装器61项、布局21/21、CAD16/16和APP UI smoke通过；包含上一轮支付恢复回归。本轮仅修改恢复存储外围及测试，不修改TaskManager、翻译算法、计费和CAD执行核心。
- 已正常交付D:\DWGC2E\release，版本2.1.1+ui.20260916-124103.a3f6f1f；SHA256 24A661D4520E41E2CB95C471B47E09101BF2C59DAA2BA69FC7CE923813E4C846。安装哈希匹配，3份原有便携数据不变，仅保留最新候选/安装包和一份上一版本回滚备份。
- 证据artifacts/cross-end-completion-20260916/task-recovery：publish.log、portable-before.json、delivery-verification.json。早期core-tests.log为补齐最终两项用例前的193/193结果，最终以完整publish.log中195/195为准。
- A02未全部关闭：实际磁盘耗尽/中断写入、账号切换压力、输出冲突、非法状态/检查点元数据及恢复反馈仍需继续审查与验收。其余G02/R01/S01/W01和真实跨端、CAD、支付/邮件范围不缩减。
## G02 语言切换专项与交付（2026-09-16 12:48）

- 扩展APP UI smoke，以延迟的隔离HTTP读/写请求验证：云端下载等待、合并预览和提交等待期间，源语言/目标语言切换均被阻止并显示说明；提交仍在原语言上下文完成。
- 验证空闲后目标语言与源语言可实际切换、加载对应本机词库；新语言的合并预览保留云端条目建议，取消不发起写入也不改变服务器内容；最后恢复原语言。原有账号切换、冲突及草稿保留回归继续通过。仅增加测试，没有重写同步实现或核心算法。
- 完整Publish-Desktop通过Core195/195、安装器65项、布局21/21、CAD16/16、APP UI smoke；已更新release到2.1.1+ui.20260916-124619.a3f6f1f。SHA256 0D72962152FE3894E30604709808F12BA0CBA13D8323E2253CA5119DC3E62092，安装哈希一致、3份便携文件未变、仅1份回滚备份。
- 证据artifacts/cross-end-completion-20260916/glossary-language/publish.log、portable-before.json、delivery-verification.json。未进行生产账号APP→网页→APP写入往返，不能用本轮隔离测试关闭真实跨端验收。

## R01a 本地候选发行清单（2026-09-16）

- 新增tools/New-DesktopReleaseManifest.ps1，显式接收本地release/candidate目录、ZIP和全新输出路径；读取build-info及CAD平台，不猜测公开下载版本。
- 在生成前核对本机EXE与build-info哈希、ZIP内版本/修订/哈希与本地一致、ZIP平台一致，以及EXE、CAD插件及插件Core DLL的逐字节哈希和长度；缺失/重复条目、篡改、平台或版本错配一律拒绝。输出记录完整构建版本、win-x64、实际CAD平台及安装包SHA256/长度。
- 候选明确标为local_candidate、public_download_authorized=false、download_url=null、mandatory=false，且不覆盖现有输出文件。不上传、不修改网站或Worker公开版本，不伪造下载地址。
- tests/BuildPipeline/Test-ReleaseManifest.ps1九项通过：正常一致性、非公开约束、既有输出保护、EXE篡改、插件错包、版本错包、平台错包、不支持平台、重复EXE条目。
- 对本次已安装APP和实际ZIP生成candidate-20260916-124619.json，证据在artifacts/cross-end-completion-20260916/release-manifest，含tests.log。此清单仅证明本地候选一致，不证明旧公开安装包版本或真实性。
- 调用示例（输出文件必须不存在）：tools/New-DesktopReleaseManifest.ps1 -PublishDir release -PackagePath artifacts/DwgTranslator-win-x64-GstarCAD-20260916-124619.zip -OutputPath artifacts/cross-end-completion-20260916/release-manifest/candidate-20260916-124619.json。
- R01b仍待明确公开发布授权和公开下载文件核验：现有官网site-config版本1.0.0/旧下载入口与Worker配置、当前本地2.1.1构建尚未统一；不得仅改版本文字宣称发行闭环。今后本地候选变动须重新生成对应清单，不能复用旧校验值。
## S01 页面安全策略真实浏览器门禁（2026-09-16，未上线）

- 新增 tests/Integration/security-browser.cjs：本地HTTP服务实际发送候选_headers中的CSP，而不是只检查配置字符串。隔离全部外部网络，不使用真实账号。
- 候选16个HTML页面在390/1280两种宽度均加载无页面异常及CSP违规；其中受保护页的匿名跳转不等同登录态全页面验收。
- 验证正常首页内联启动脚本、同源API可执行；注入script、onclick、动态函数和外部连接分别产生对应CSP违规并被阻止。动态函数探针通过真实同源JS加载，避免DevTools evaluate绕过eval限制产生误判。
- 安全专项4/4；当前网站工作区所有内联脚本哈希也单独核对1/1通过。仅此哈希检查不代表整个脏工作区可发布。
- cloud-browser.cjs现在要求并实际发送网站CSP，实际Worker路由+隔离SQLite的16项术语/历史/会话集成用例连续两次16/16通过。第一次运行出现ERR_ADDRESS_IN_USE和请求等待失败，原始失败日志保留；未修改业务代码或放宽断言，独立重跑两次通过，环境间歇故障原因仍未确定。
- 证据：session-security/browser-tests.log、worktree-policy-check.log、cloud-browser.log（初次失败）、cloud-browser-recheck.log、cloud-browser-confirm.log。
- 当前仍为本地候选，未发布Pages，未修改APP或Worker核心。上线前仍需远端基线/配置核对及最小差异审查，之后验证线上响应头；S01凭证生命周期和全登录态矩阵仍未全部完成。style-src保留unsafe-inline、img-src允许HTTPS二维码是明确残余边界，CSP不替代输出转义或HttpOnly凭证设计。

## S01 凭证过期边界修复及安全策略上线（2026-09-16）

- 新增 tests/Integration/session-lifecycle.test.mjs。对原生产适配层复现4项失败：非字符串token仍成为Bearer、过期/非法日期凭证仍发送、等待请求期间过期仍交付成功响应。此前退出和换token防护有效，不夸大为跨账号认证绕过。
- 候选仅将api-client.js的readToken改成校验字符串类型及有效期，保留无expiresAt的旧会话兼容；未复制工作区新增认证框架、全局请求门禁或其他页面改动。已过期凭证不附加到请求，服务端仍负责最终授权。请求等待期间凭证失效由既有session_changed处理拒绝旧数据。
- 最终12/12会话测试通过，覆盖退出/换账号后的200与401、过期/非法存储、替代Bearer不清理当前账号、匿名登录失败不清理账号，并通过可控时钟验证不修改存储也能拒绝过期期间返回的内容。候选浏览器安全4/4、真实Worker隔离集成16/16继续通过。
- 发布前验证原生产Pages 1670cfae-3bc7-4e08-8a02-a6febc10641b及62个静态文件与基线一致，所有Functions源码不变，候选文件差异仅public/_headers和public/js/api-client.js。生产及预览deployment_configs不变。
- 已发布Pages版本9c857a8a-8157-4cfe-bf80-838799d58b2c；部署工具成功且管理API确认canonical_deployment成功。自定义官网域名62个静态文件哈希匹配候选，16个HTML的实际CSP响应头匹配，网页代理health200/匿名glossary401/history401验证通过。上线后生产及预览配置仍完全一致。
- 证据：session-security/session-before.log、session-final.log、cloud-session-final.log、browser-final.log、preflight.json、pages-deploy.log、production-verification.json。*-private.json含配置，不公开。网络TLS重置仅对读取做最多3次重试，断言失败不重试也不跳过。
- 本轮未更新APP、未部署Worker业务代码、未更换密钥、未操作真实账号/订单/邮件/下载。S01仍不完全关闭：APP凭证生命周期、Worker端全会话撤销及登录态全页反馈仍须审查；CSP保留内联样式与HTTPS图片兼容范围，sessionStorage凭证仍可被获准同源恶意脚本读取，不将CSP描述为彻底消除XSS。

## A02 存储中断边界及正常快照抑制（2026-09-16 13:15）

- 复现并修复两项存储外围缺陷：损坏原文件被外部移除后，遗留恢复标记会让后续健康队列产生多余recovery副本；Save对任务枚举发生的异常原先在保护范围之外，会逃逸给调用方。对应新增测试修复前2项失败、修复后通过。
- 保存改成唯一CreateNew临时文件、完整写入、Flush(true)后才原子替换/移动；不使用覆盖式复制，不改TaskManager调度、翻译/计费/CAD核心。此措施降低写入未落盘风险，不宣称硬件断电或磁盘耗尽已经实测。
- 新增中断临时文件场景：截断的遗留tmp不被当作主记录恢复，不覆盖已提交队列；正常后续保存仍有效，也不擅自删除其他写入者遗留文件。总Core198/198通过。
- 完整Publish-Desktop（未跳过任何门禁）通过：安装器65项、Core198、布局21、CAD16、APP UI smoke。已更新D:\DWGC2E\release到2.1.1+ui.20260916-131334.a3f6f1f，SHA256 B72BA4C3670F0AEB9B770E6BB78F4C14465652F310A94358AC93D01E87448F56；安装哈希一致、3份便携数据不变、仅保留1份回滚及最新候选/包。
- 重新生成当前版本本地发行清单，避免沿用旧包校验值。证据task-storage-boundaries/before.log、after.log、publish.log、delivery-verification.json、release-manifest.json。
- A02未关闭：实际磁盘满/进程强制中断、损坏检查点元数据、账号切换压力、输出冲突及恢复专用UI仍待处理。只读审查另发现SwitchAccountStore在旧队列保存失败时仍清除内存并切换，需与APP会话切换流程一起加回归和最小修复；不可把本轮存储测试当作账号切换安全证据。

## A02 账号切换保存失败保护及交付（2026-09-16 13:22）

- 修复SwitchAccountStore原先在旧队列Save报告失败时仍清空队列的问题：保存失败现在阻止切换，保留原store、内存任务和恢复待确认列表。新增EnsureAccountStoreSaved仅供账号边界预检查，不修改翻译执行/计费/CAD算法。
- APP登录/退出在远端认证或注销前先保存旧队列；完成认证后，最终切换还会复检保存。会话持久化通过切换提交回调执行，发生设置写入异常时不先清空原队列、不更换任务store。目标目录/导出目录在提交会话前创建，避免目录失败留下新账号配旧队列。
- 登录、退出和过期清理统一使用该切换顺序；保存失败反馈明确说明磁盘/权限问题。过期场景仍撤销本机已验证状态，不把保留恢复数据理解为仍获授权。
- 新增Core3项：保存失败不调用会话提交且可以重试、会话提交抛错保留原store及PendingFromLastRun、50轮A/B往返不混合所有者。Core201/201通过。
- 新增实际APP界面专项：分别在登录前和已登录退出时锁定真实任务文件，验证请求/会话/设置原字节不变、内存队列保留、明确提示、解锁保存恢复。此处FakeApi不实现远端Logout接口，不能声称已验证真实服务器撤销或退出后网络中断。
- 完整发布通过安装器65、Core201、布局21、CAD16及UI smoke；D:\DWGC2E\release已更新2.1.1+ui.20260916-132011.a3f6f1f。EXE SHA256 CB23A031B72B8E336AA81C94C1CAC3A0E8CAB9F237B1FF547E6428D8C443AB62；安装哈希一致、3份便携数据未变、仅一份回滚、当前版本发行清单重建。
- 证据account-switch/core-tests.log、publish.log、portable-before.json、delivery-verification.json、release-manifest.json。
- 剩余：实际磁盘耗尽/强制中断、非法检查点和输出冲突、恢复专用UI；账号压力目前为隔离内存store50轮及真实文件锁UI，不替代生产跨账号同步或真实远端注销竞态验收。其他S01/W01/CAD/支付/邮件/发行范围保持未关闭。

## A02 异常检查点与单条损坏隔离（2026-09-16 13:27）

- 新增11项用例，修复前分别8/8、3/3复现失败。读取时拒绝未知任务状态/空或重复任务ID，避免异常状态被默认为待执行；不改变正常12种状态恢复规则。
- 检查点列表为null、含null条目、空Handle、重复Handle、null原文/译文、未知译文状态或null签名时，禁用该行断点复用并清空签名/译文缓存，不让这些结构进入实体查询/回写。正常任务仍保留；已完成历史不自动重跑。原始异常文档在下一次保存前保留为唯一恢复证据，不产生正常日常快照。
- 任务文件合法JSON中单条记录字段类型/日期损坏时，按行隔离，其他健康任务继续恢复。整体语法截断仍按损坏文档保护，未尝试猜测性拼接。
- 完整发布通过Core212/212、安装器65项、布局21、CAD16及APP UI smoke；没有修改TaskManager翻译/计费/CAD执行逻辑。只修改JsonTaskStore读取校验与测试。
- D:\DWGC2E\release已更新2.1.1+ui.20260916-132535.a3f6f1f，EXE SHA256 BB53A1D7B60F5CA12F0B917EBBD7B59803EAE24F7BB2499EA7F1CC42783C1839；安装校验通过、3份便携数据保持不变、仅1份回滚，当前版本清单已重建。
- 证据checkpoint-validation/before.log、row-before.log、publish.log、delivery-verification.json、release-manifest.json。after.log是单条损坏隔离补齐前209/209，最终以publish.log中212/212为准。
- 检查点校验不等于验证译文本身正确或所有语义组合安全；依旧依赖现有运行时签名匹配（图纸/语言/配置/云端上下文）防止跨环境复用。A02恢复专用UI、磁盘满/强制中断实测和输出执行阶段冲突仍待完善，其他跨端范围继续保持。

## A02 输出执行阶段冲突验证（2026-09-16）

- 审查实际写入路径：离线DwgWriterService与CAD插件AcadWriterEngine均先序列化到临时文件，再调用SafeFileCommit。非显式覆盖使用不覆盖的File.Move；显式覆盖已有文件使用File.Replace，未发现需要重复修改核心逻辑的问题。
- 新增WriterOutputConflictTests共8项，生成真正DWG/DXF源图并调用实际读取/写入服务：在规划完成后创建外部同名目标，分别验证skip/rename拒绝覆盖并返回失败、overwrite明确覆盖且重新读取到译文；验证目标独占锁下源图/既有输出字节不变、临时文件清理。skip/rename在执行阶段遇冲突是安全失败，而不是自动重新规划或默默跳过。
- 新增12个并发非覆盖提交竞争用例：恰好一个成功，最终文件与胜者一致，失败者临时文件保持可恢复。此测试验证文件提交层，不冒充12个CAD进程的实测。
- Core全套221/221通过，证据artifacts/cross-end-completion-20260916/output-conflicts/core-tests.log。
- 本轮仅新增测试，没有改生产代码，没有构建APP或替换release；安装版本仍为2.1.1+ui.20260916-132535.a3f6f1f。不因测试项目构建声称已交付新版APP。
- 边界：CAD插件本轮只核查其共享提交调用，未运行真实CAD进程冲突测试；实际磁盘满/强制中断、恢复专用UI、S01/W01及真实跨端/CAD/支付/邮件验收继续未关闭。

## A02 真实进程中断与重开持久化验证（2026-09-16）

- 新增tests/TaskStoreCrashProbe独立测试进程，调用真实JsonTaskStore，在专属随机临时目录运行；父进程仅终止自己创建的子进程，不操作用户APP、账号或图纸。
- 7/7通过：枚举保存输入前中断1次、观察到空临时文件后中断2次、观察到非空临时文件后中断3次、确认提交后中断1次。前三类实测保留原提交，确认提交后恢复96条完整新记录；每次重开继续保存/读取成功，均无混合/截断提交，也未错误生成损坏恢复备份。
- 非空临时文件约25MB，观察到长度不等于证明在系统Write调用内部终止；存在观察到终止之间的调度竞争，判据要求旧版或新版完整一致。本测试不是硬件断电、物理磁盘耗尽、真实CAD进程终止或完整APP重启交互验收。
- 证据artifacts/cross-end-completion-20260916/task-interruption/probe.log；Release配置Core221/221通过，core-tests.log。发布脚本已增加该探针门禁，脚本语法校验通过；本轮没有执行APP发布或替换release，没有生产代码变更。
- 新确认剩余项：强制中断会遗留tasks.json.<guid>.tmp，重开正确忽略但尚无生产清理策略。需要谨慎区分未提交临时文件与.recovery恢复证据，后续补有边界的清理与测试，不能泛化删除。
- A02实际磁盘满、完整APP/CAD中断恢复UI及临时文件策略仍未关闭；S01/W01及真实云同步/CAD/支付/邮件/公开版本对齐保持原有范围。

## A02 异常恢复专用界面与本地交付（2026-09-16 13:45）

- JsonTaskStore新增可选恢复诊断，读取失败、异常任务行或非法断点时保留本次工作区警告；TaskManager只转发该诊断，没有修改翻译/计费/CAD执行算法。
- 批量任务页新增非模态恢复警告，包括核对任务列表、保留原记录/恢复证据的说明与实际任务目录。即使完全损坏导致零条任务，也不会仅显示空列表而无说明。成功保存不会立即隐藏此前的数据损坏提醒；切换到健康工作区后更新并清除旧账号提示。不增加正常快照、不自动删除恢复文件。
- 新增Core5项：整文件损坏、null行、未知任务状态、异常检查点均有警告，安全保存后证据仍在；缺失/健康文件无误报。Core226/226通过。
- 实际APP UI smoke新增损坏空队列警告显示、真实目录、布局范围、保存保留原字节、账号切换清除提示检查；已检查task-recovery-warning.png，警告文字换行可见且不遮挡工具栏。
- 首次发布因另一UI smoke进程32244占用测试程序集而退出，release保持不变；再次检查确认该进程已退出（未终止其他任务），完整重跑通过。期间另一个交付将release推进到134135，以当时实际release作为后续回滚基线，未回退其他任务改动。
- 最终完整门禁：安装器65、Core226、进程中断7、布局21、CAD部署16、APP UI smoke全部通过。发布脚本正常运行，无BuildOnly/SkipTests。
- 当前D:\DWGC2E\release版本2.1.1+ui.20260916-134303.a3f6f1f，SHA256 5DBFC49818E3FD61795D2B8DFAEFB1171599728AB4671557C3600082152B2E40；安装哈希复核通过、3份便携数据原哈希不变，仅1份回滚，当前包与本地发行清单重新验证。
- 证据recovery-notice/core-tests.log、publish.log、publish-lock-failure.log、task-recovery-warning.png、delivery-verification.json、release-manifest.json。
- 未声称A02整体完成：实际磁盘满、完整APP/CAD强制中断恢复、遗留临时文件有界清理仍需处理；恢复提示目前在批量任务页，不包含一键修复/自动恢复损坏字节。S01/W01与生产同步/CAD/支付/邮件/公开下载版本对齐仍按原范围推进。

## S01 APP/Worker凭证生命周期扩展验证（2026-09-16）

- 新增Worker生命周期矩阵12项：过期/非法有效期/已撤销/缺客户端上下文/账号禁用/APP绑定撤销6种状态，在10个受保护读写入口均返回401。覆盖资料、词库、历史、设备、密码修改、订单及翻译入口，且无词库或翻译写入。
- 密码重置调用实际Worker处理函数：同账号APP/网页旧会话均失效，其他账号不受影响；原密码不可登录，新密码可登录；验证码不可重放。错误/过期/错误用途验证码不改变密码和有效会话；同验证码并发重置只有一个成功。
- 注入会话撤销事务失败，要求HTTP500且不泄漏数据库故障细节；确认验证码消费、密码更新、会话撤销一起回滚，移除故障后可用同验证码恢复。初次测试错误预期异常外抛，经核对入口统一错误处理后改为验证真实500契约，并保留完整回滚断言，未修改生产代码。
- 新增真实workerd/D1完整Worker路由集成1项：验证退出仅撤销当前会话、撤销设备不影响另一账号、重新绑定不会复活旧会话、密码重置失败原子回滚与并发单胜者。使用完整schema，显式禁止外部fetch，不发送邮件/调用付费服务，不接触生产账号。
- APP新增10项实际WorkerApiClient测试：退出仅对成功/401承认完成，403/500/网络故障不假报服务端注销；旧资料请求的200/401在账号切换/本地清空后丢弃；旧退出响应不作用于新会话；不污染HttpClient默认认证头。
- 完整Worker239/239通过、类型检查通过；APP Core236/236通过。证据credential-lifecycle/matrix.log、d1.log、worker-tests.log、typecheck.log、core-tests.log。
- 本轮只有测试变更，无Worker部署、无APP构建或release替换。既有实现通过上述范围，无需为测试重复改核心代码。仍不能将隔离运行时证据当生产授权账号验收。
- S01剩余：真实APP界面远端注销失败/注销成功后本地保存失败的用户状态与恢复、所有网页登录态页面的交互矩阵、生产账号撤销往返；原A02/W01/CAD/支付/邮件/公开版本范围不变。

## S01 云端退出后本地保存失败修复与交付（2026-09-16 14:01）

- 已修复真实APP账号状态缺陷：服务端确认退出后，立即清除本机已验证状态、资料/权益/设备显示并递增会话代次，再尝试本地工作区切换。后续配置或任务保存失败不能恢复已撤销的登录权限，旧代次异步结果不能重新应用。
- 保留原任务队列及未能更新的本地会话文件以便恢复，不把本地失败误报为云端未退出。明确提示“云端已退出、本机权限已撤销、任务已保留、检查磁盘/权限后重试退出”。远端未确认时保留原状态，不宣称注销成功。
- 新增真实ViewModel UI smoke：远端false/网络异常、远端成功后真实settings文件独占锁、远端成功后真实任务文件独占锁、清除显示与会话代次、保留队列及配置原字节、解除占用后重试与重新登录。原保存预检测试新增断言：预检失败不发远端退出请求。
- 初次门禁准确发现网络IOException被误提示为保存失败；增加远端请求阶段区分后完整重跑通过。首次失败未替换release。
- 完整发布门禁通过：安装器65、Core236、进程中断7、布局21、CAD部署16及APP UI smoke。使用Publish-Desktop.ps1，无BuildOnly/SkipTests；没有修改翻译、计费或CAD执行核心，没有生产账号操作或云端部署。
- 已安装D:\DWGC2E\release：2.1.1+ui.20260916-135929.a3f6f1f，SHA256 FF6769226770C9B0EB11612DFB19E313CCB41C399A20B0F9AF80E38AF31CF075。安装哈希与包清单复核通过，3份便携文件哈希不变，仅保留一份回滚。并发任务此前交付的135439已由正常保留流程处理，未手工回退其他任务更改。
- 证据：artifacts/cross-end-completion-20260916/logout-recovery/{publish.log,initial-regression.log,portable-before.json,delivery-verification.json,release-manifest.json}。
- 仍未关闭整个S01：网页登录态完整交互及授权生产会话撤销待验收。A02真实磁盘满/完整APP-CAD中断/遗留临时文件有界清理、W01、授权生产同步/CAD/支付/邮件及公开下载一致性仍保持原范围。

## S01/W01 网页会话全页矩阵、历史回归与上线（2026-09-16）

- 以已上线Pages 9c857a8a的隔离副本作为基线，不直接发布脏网站工作区。浏览器实际复现：账户中心/资料/设备/购买4页在移除、替换或过期会话后，focus事件未及时收起旧账号内容；基线20项中12项失败，历史/术语的同类8项通过。测试初始化曾在导航时错误重置账号，已修正为每个隔离页会话仅初始化一次，baseline.log为修正后的有效证据。
- 定点修复4个外部脚本：account.js校验已展示账号与当前会话及有效期，失效后隐藏旧面板而不删除新账号凭证；portal.js读取当前有效期并在恢复/焦点/存储变化及静置检查时撤销表单与设备显示；billing.js增加有效期和空会话校验、立即关闭旧二维码/清除旧订单和会员显示，阻止旧购买页继续动作；cloud-session.js补静置过期和可见性检查。静置使用1秒检查，不新增服务器请求；浏览器后台节流时由回到前台事件立即补检。
- 同样的锚点小改动同步到D:\DWGC2E_Website\js，对该工作区其他新界面代码保持原样。未改CF Worker、数据库、支付计费事务、翻译或CAD核心。
- 永久浏览器测试tests/Integration/WebsiteProductionSessionLifecycle.cjs：6页×3种状态×focus/pageshow/storage共54项、6页静置过期、历史延迟200/401不覆盖或删除新账号2项、320/390/1280px历史分页失败重试/保留已加载数据/旧记录/搜索/HTML与非HTTPS下载保护3项、退出失败再成功1项。实际候选CSP生效，外部请求全部阻断，API全隔离；66/66通过。扩展测试曾发现历史/术语静置遗漏2项，补齐后全部通过。
- 网站工作区相关API、次级页与支付界面回归25/25通过。没有真实账号、邮件、下单或扣费；小屏窗口检查不等于真实移动设备/完整键盘验收。
- 发布前线上62文件全部与旧隔离基线一致，候选恰好只改4个JS；代理Functions、_headers/CSP、路由保持原样。刷新已有Cloudflare登录凭证后发布，未打印/轮换密钥。
- 新Pages生产部署c7767673-53c6-4533-a6b6-396ac48d5eb3。自定义域名62个文件与候选哈希逐一一致、所有HTML的CSP一致、deployment_configs与发布前一致；健康200、匿名词库401、匿名历史401验证通过。只上传4个变更静态文件。
- 证据：artifacts/cross-end-completion-20260916/web-lifecycle/{baseline.log,expanded.log,final-browser.log,worktree-regression.log,preflight.json,pages-deploy.log,production-verification.json}，私有配置快照不公开。
- 本轮无APP构建/release替换；本地仍为2.1.1+ui.20260916-135929.a3f6f1f。仍保留完整剩余范围：生产授权账号生命周期和APP→云端→网页→APP同步、其他交互/键盘/设备/离线矩阵、A02实际磁盘满与完整APP/CAD中断及遗留临时文件清理、真实CAD/支付/邮件及公开版本对齐。不能凭本次隔离66项关闭整个S01/W01。

## A02 所属进程可验证的临时任务文件清理与交付（2026-09-16 14:26）

- 新增TaskTemporaryFiles隔离清理模块，JsonTaskStore仅改临时文件命名与成功提交后的维护调用；正式tasks.json、正常快照策略、任务执行算法、翻译/CAD/计费事务不变。新临时名包含PID+进程启动时间+随机ID，避免PID复用误判。
- 清理仅在任务新状态已成功原子提交且本次store未出现读取恢复问题时运行；仅当前任务文件所属目录、不递归，最多检查128文件/删除16文件。只删超过24小时且所属进程已结束（或PID已被新进程复用）的新格式候选；独占打开并按句柄关闭删除，被占用/只读/近期/未来时间/来源未知/状态不可查询均保留。跳过重解析目录及文件；旧GUID临时格式、.recovery证据、其他工作区和子目录不泛化清理。
- 新增10项执行通过的Core用例：旧已结束候选、活动进程、近期/未来时间、独占锁与重试、只读、提交失败不清理、损坏恢复保留证据、16个删除上限、旧格式/畸形/其他工作区与子目录保护。
- 另1项符号链接真实运行测试因主机Win32 1314（无创建符号链接权限）导致首次发布门禁失败，release保持原样。明确标注Skip而非通过，保留可在具备权限环境启用的测试；链接运行时验收仍未关闭，不能用属性检查替代真实验证。
- TaskStoreCrashProbe增强：杀死仅本测试创建的子进程后，真实临时候选在首次重保存仍保留；仅将该隔离目录候选时间推进至2天前，再次保存自动清理。7/7通过，保持原/新完整队列验证。没有终止用户APP/CAD，没有清空真实磁盘。
- 并行任务完成的PendingPurchaseStore及Models/PendingPurchase机械提取、11项Core测试和BillingPersistenceSmoke字段更新均保留；正常发布包含这些改动，支付持久化UI冒烟通过。未重复运行或覆盖对方修改。
- 正常Publish-Desktop.ps1完成：安装器65、Core257通过/1明确跳过（总258）、中断7、布局21、CAD部署16、APP UI smoke通过；无BuildOnly/SkipTests。
- 已安装D:\DWGC2E\release：2.1.1+ui.20260916-142413.a3f6f1f，SHA256 CF433C62EB09CD5A2219E832C136EFCCB2713A5F935C957277237BF94CD4FA34。安装哈希和发行包清单复核通过，3份便携数据哈希不变，当前候选/包与一份回滚保留。
- 证据：artifacts/cross-end-completion-20260916/temp-cleanup/{core-tests.log,crash-probe.log,publish.log,publish-link-privilege-failure.log,link-test.log,delivery-verification.json,release-manifest.json}。
- 限制：不自动删除旧版本无所属进程信息的临时文件；目录前128项均不可清理时不会越界扩大扫描，超大历史目录需单独审查。这是保守维护，不代表全部旧遗留已移除。A02真实磁盘满、完整APP/CAD中断恢复及符号链接环境验收仍待补；原S01/W01/生产同步/CAD/支付/邮件/公开版本范围保持。

## W01 历史记录键盘/断网与资料设备重试扩展（2026-09-16）

- 在已上线c7767673对应的隔离副本新增8项永久浏览器测试：历史首读断网、已加载后断网及详情重试2项；320/390/1280宽度只用Tab/输入/Enter完成搜索及详情刷新3项；列表加载中重复点击只发一请求1项；资料/设备断网保留旧显示、恢复后重试2项。
- 初次键盘测试错误要求搜索后只按一次Tab即到行按钮；核查表格滚动容器本身有可访问停靠点后，改为有限Tab步数可达并真实Enter刷新，不修改页面绕过焦点顺序。新增场景与延迟响应共10项针对性真实本地HTTP测试通过。
- 扩大完整回归曾出现Windows环回ERR_ADDRESS_IN_USE及两个延迟响应等待超时，未当作产品回归通过。首轮无上限等待仅终止本次创建且命令行核验一致的两个node测试进程，没有终止APP/其他任务；随后每项增加15秒超时。
- 增加可选LOCAL_ASSET_ROUTES=1：由Playwright按原路径提供候选原始静态文件字节和实际CSP，避免环回连接耗尽，API仍全隔离。此模式完整74/74通过；默认真实HTTP模式保留。不得把该证据描述为74项生产HTTP或物理设备验收。
- 本轮没有发现需要修改生产业务代码的问题，仅完善测试与文档；无APP构建/release替换、无Pages/Worker部署、无真实账号/邮件/支付操作。原目标未关闭。
- 证据artifacts/cross-end-completion-20260916/history-interaction/{targeted.log,new-cases.log,portal-offline.log,final-browser.log,local-assets-browser.log}。其中final-browser.log是明确保留的失败环境证据，不是最终通过日志。
- 其余剩余项包括实际磁盘满/完整APP-CAD中断/符号链接权限环境验证、真实授权账号云同步、更多站点与设备交互、真实CAD/支付/邮件及公开版本对齐，仍按完整范围推进。

## W01/P02 支付页断网、重载与订单切换浏览器验收（2026-09-16）

- 前一目标轮仅答复剩余范围，没有实施；本轮重新检查工作树及环境后推进隔离验收，未将目标缩减为已有通过项。
- 真实磁盘满安全预检：当前进程非管理员，未发现New-VHD/Mount-VHD/imdisk工具。不填满用户卷、不把权限失败当磁盘满测试、不修改系统权限；该项仍待隔离容量环境。证据billing-interaction/disk-full-preflight.json。
- 永久浏览器矩阵新增6项支付页场景：下单响应丢失后原页重试/重载后重试2项（断言原幂等标识与请求体一致）；确认付款断网后保留订单并恢复权益显示且不新增下单1项；过期/创建结果未知订单不展示可付款二维码2项；选择新订单后旧订单延迟返回不得覆盖1项。
- 使用已部署页面对应的隔离副本，所有API均拦截为测试响应；二维码为不可付款的测试文本，禁止外部网络，不登录真实账号、不调用支付平台。此次未发现需修改业务代码的问题。
- 新增6项默认本地HTTP测试6/6通过；全量80项在LOCAL_ASSET_ROUTES=1模式80/80通过，0失败/取消/跳过。静态资源路由模式不是生产HTTP验收。证据billing-interaction/{targeted.log,http-targeted.log,full-browser.log}。
- 本轮仅修改tests/Integration/WebsiteProductionSessionLifecycle.cjs及测试说明/进度文档，无APP构建、release替换、Pages/Worker部署。支付实际回调/对账/补偿、生产云同步、真实CAD及公开发行等完整剩余范围保持未关闭。

## A02 真实APP恢复提示重启验证与取消误清理修复（2026-09-16 16:06）

- 使用当时已安装142413版本、独立DWGC2E_DATA_DIR与10个自建任务记录（6未完成状态+4终态）。真实APP启动提示6个未完成任务并显示等待状态；只终止核对PID/启动时间/EXE路径的本测试进程，再次启动同样提示6项，任务JSON及全部测试图纸字节不变。没有操作真实账号/用户CAD或图纸。此为预置状态的APP启动/中断恢复，不是CAD执行中断验证。
- 发现真实交互缺陷：恢复提示写继续/取消但按钮实际是确定/否；ConfirmDialog的布尔返回把关闭/取消变成false，恢复分支随后Clear(includeUnfinished:true)，会误清恢复队列，且该Clear也清理终态历史。
- 最小修复只涉及APP交互外围：PromptDialog增加可选按钮文字，默认调用语义不变；ResumePendingTasks使用三态结果，继续处理/清除未完成记录/暂不处理明确区分。关闭/取消保留；只有显式No才通过现有Remove逐项移除本次pending快照，不改TaskManager核心，保留已完成与已取消历史。
- 新增RecoveryDecisionSmoke，直接调用真实MainViewModel恢复入口，混合未完成/已完成/已取消记录验证：关闭和暂不处理保留内存队列及文件原字节、不自动启动；显式清除只移除未完成项、持久化终态历史保留；三个按钮文案正确。现有默认未保存对话框回归仍通过。
- 原生窗口可访问树已确认修复后的三个按钮。图像捕获SetIsBorderRequired返回0x80004002，主窗口Esc未能投递到模态窗口，点击提示coordinate input geometry is unavailable；这些原生输入没有记为通过，保留边界。WPF事件及Close路径由UI smoke验证，物理键盘Esc验收仍未证实。
- 首轮155847通过交付后，补充终态历史保护并再次正常发布；最终release为2.1.1+ui.20260916-160406.a3f6f1f，SHA256 26174377BADD5686D96A8C5F62BA3437A000E9564419BD7693D2CEE79D601634。未使用BuildOnly/SkipTests。安装器65、Core259通过/1明确跳过、进程中断7、布局21、CAD部署16、UI smoke通过。安装EXE/ZIP清单一致，3份便携文件哈希不变；保留最新候选/包与上一已安装版本回滚，清理中间构建。
- 证据artifacts/cross-end-completion-20260916/app-process-recovery：first-window.txt、restarted-window.txt、fixed-prompt.txt、verification.json、publish-final.log、delivery-verification-final.json、release-manifest-final.json。publish.log及无final后缀的delivery记录对应中间155847版本，不作最终版本证据。
- 全目标保持：真实磁盘满、完整APP/CAD执行中断、符号链接权限环境、真实账号云同步及会话撤销、更多网页/物理设备、真实CAD矩阵、支付/邮件/公开发行仍未关闭。本轮无Pages/Worker部署。

## S01 云端撤销会话后的真实网页联动（2026-09-16）

- cloud-browser永久用例新增6项：账户、资料、设备、历史、术语五页在服务端logout后，下一次真实API操作返回401并清理网页凭证/敏感显示；另验证失效术语会话不能保存草稿到云端。APP及其他账号会话不受误伤，词库保持不变。
- 修正测试初始化只在每个标签页首次注入凭证，避免页面跳转后重新注入已撤销token、掩盖实际退出行为。首次定向失败是测试把术语创建201误写成200；修正后6/6通过。
- 本轮全量22/22通过，0失败/取消/跳过。实际Edge及本地HTTP页面调用真实Worker handler与隔离SQLite适配器；不是生产账号验收，也不是浏览器直连真实D1/workerd。证据server-revocation-browser/full-browser.log、targeted-corrected.log。
- 本轮仅补测试及文档，无生产业务改动、APP构建或云端部署。重新核对release EXE SHA256为26174377BADD5686D96A8C5F62BA3437A000E9564419BD7693D2CEE79D601634，与160406交付一致。
- 未关闭：生产账号云同步/会话撤销、真实磁盘满、完整APP/CAD执行中断、符号链接权限环境、更多网页及物理设备交互、真实CAD矩阵、真实支付/邮件和公开版本下载对齐。不能将隔离回归通过视为这些事项完成。

## S01/P02 购买页服务端会话撤销联动（2026-09-16）

- 延续上一轮已产生22/22证据的有效进展，本轮补齐购买页而非仅复述状态。新增查看订单、确认支付状态、隐藏订单三条浏览器→真实Worker处理器的失效会话路径。
- 独立SQLite直接置入不可付款的测试订单；在网页已显示订单（确认场景已打开订单面板）后，通过真实logout撤销网页会话。下一次操作实际返回401，页面凭证、订单列表、二维码和购买按钮清理，付款面板隐藏，登录入口可见；APP会话仍可用。
- 三项逐一比对orders/subscriptions/payment_events/payment_settlements/payment_order_hidden原始数据，均未变化。没有真实支付平台配置、生产账号或生产数据库操作；不代表真实支付验收。
- 新增3/3、全套25/25通过，0失败/取消/跳过，使用默认本地HTTP而非静态路由替代。证据server-revocation-browser/billing-targeted.log、full-browser-25.log。
- 本轮无生产业务缺陷需要修改，仅扩充永久回归及文档；无APP构建/release替换/云端部署。生产往返同步、真实历史记录、CAD完整执行与故障恢复、磁盘耗尽、公开发行、真实支付邮件及设备交互仍未关闭。

## A01/A02 真实浩辰2024回写与输出保护（2026-09-16 16:24）

- 上轮25项浏览器回归属于有效进展；本轮转向真实CAD执行。使用独立生成图纸、当前release/CadPlugin插件和本机浩辰2024，未调用云翻译、未打开用户图纸、未修改核心算法。新增可复用tests/CadIntegration/Test-CadRuntimeWriteback.ps1及README。
- 真实宿主执行DBText/MText各3种角度（0/90/约40度），首次生成及回读确认6/6文字内容准确。原始文字框检查实际0/6，报告完整保留；现有可用空间检查6/6，新增/全部文字重叠均0。两项标准不同，不将后者当作前者通过，也不声明视觉可读性/字号不变；严格原框及视觉要求仍需单独确认。
- 真实DwgTranslateWrite四项完成信号与session对应：正常success 6/0；已存在输出failed 0/6；输出=源文件failed 0/6；缺失源文件source_missing 0/0。源文件与已存在输出SHA256逐字节不变，成功输出非空，缺失源文件未产生输出，无遗留*.tmp.dwg。正常输出再次经真实宿主回读，可用空间6/6且无新增重叠。
- 三个永久脚本启动的宿主均正常退出；最终进程核查没有gcad.exe，不强杀其他进程、不调整系统信任设置。初步探针也已正常结束。release插件SHA256=0890F7318FB3C2007797717C87C6673958EAD5A290D3A84CAA7AFF776ABA072A；APP EXE仍为160406交付哈希，无新构建/部署。
- 证据cad-runtime/command-acceptance.log、command-acceptance/verification.json、*.done、layout-request.txt.report、synthetic.report和各进程身份记录。初步探针报告保留于cad-runtime根目录。
- 这推进了真实CAD验收，不等同于APP提取/调度/云翻译全链路；属性/表格/引线/块及其他宿主、真实执行中断、磁盘满、真实生产账号/支付/邮件/公开发行和物理交互仍未关闭。

## A01 实际安装APP读取器与CAD往返缺陷修复（2026-09-16 16:34）

- 本轮承接真实CAD验证，新增tests/CadRuntimeProbe：只读解析已安装单文件APP的.NET bundle6，直接加载其中Core及托管依赖，不用重新编译的Core替身。生成的配置由真实APP读取结果构造，再交给真实浩辰2024当前release插件回写，最后由同一安装读取器读取输出。翻译内容为固定测试文本，无云端/支付请求。
- 发现实际缺陷：CAD报告success 6/0，但两处MText回读PlainText带W0.75;，原始RawText为{\\W0.75;...}。初次独立探针先因APP是单文件包、并无旁置Core.dll而失败；改为从安装包内存加载后才取得上述产品缺陷证据。两类失败日志都保留，不混淆。
- 最小修复仅DwgReaderService.CreateMTextEntity读取适配：先从解析副本移除数值宽度控制码，再沿用ACadSharp其余解码。原RawText/FormatTemplate及几何信息不动，不改翻译、计费、CAD回写或布局算法。没有用直接删正文W0.75;的方式掩盖问题。
- 永久回归最初6项5失败/1通过，修复后6/6；另补4项解码兼容（换行、Unicode、颜色/符号、普通W0.75;正文），由发布全量门禁覆盖。原始格式和文件字节保持不变。
- 正常Publish-Desktop（无BuildOnly/SkipTests）完成：安装器65、Core281通过/1明确跳过（282总数）、持久化中断7、布局21、CAD安装16、UI smoke通过。当前release=2.1.1+ui.20260916-163249.a3f6f1f；SHA256 B94C384E53C1742D3C29C7628D2C244CD925F2AF4C8477CCCF3C3C17CCD45C4D。安装/候选哈希一致，便携文件逐项相同，仅保留一个回滚目录。期间工作区已有其他任务162914构建，未回退其工作，最终日志记录本次交付。
- 新版独立目录再次执行安装读取器→真实CAD→安装读取器：6/6内容精确匹配，句柄、类型、旋转和源图字节保留，输出文件归属正确，无临时图纸泄漏，真实CAD正常退出。源/输出提取记录、安装EXE/Core/插件哈希均落盘。不是APP窗口队列调度或真实云翻译验收。
- 证据cad-runtime：reader-regression-before/after.log、reader-prepare.log（单文件定位失败）、reader-prepare-bundle.log、reader-verify.log（真实产品失败）、reader-roundtrip/output-extraction.json、reader-fix-publish.log、reader-delivery-verification.json、reader-roundtrip-fixed/{before.json,verification.json,source-extraction.json,output-extraction.json,process.json,writeback.done}、reader-verify-fixed.log。
- 全目标保持未完成：生产账号云同步及历史、APP完整调度/执行中断、复杂CAD实体与其他平台、磁盘满、符号链接权限、严格原框/视觉验收、真实支付/邮件/公开发行和物理交互均未被本轮狭义往返替代。

## A01 完整任务流程缺陷定位及发布冻结（2026-09-16）

- 上轮通过安装包真实任务流程发现新缺陷，属于有效进展。本轮只读复核原失败证据：cad-runtime/app-pipeline/task-final.json为Completed/6条译文/0失败/OutputPath=null；progress.json明确“没有可写回的译文”；app-pipeline.log在真实CAD调用断言处失败。不能把此结果当成CAD全流程通过。
- 根因定位：本地TranslationService返回TranslationPair，不改输入实体；TaskManager等待返回后只记录日志，未回填实体，BuildWritebackSet因此为空。旧FakeTranslator主动修改实体，且注释错误声称真实服务也这样做，掩盖了该接口衔接缺口。此结论限于已验证的本地TranslationService路径，不泛化为WorkerTranslationService/所有APP入口都失败。
- 本轮仅修改测试与文档：TaskManagerPipelineTests新增真实TranslationService+固定离线回复回归，要求实际调用writer、输出存在、实体译文/状态/来源正确；纠正旧替身注释；CadRuntimeProbe在finally新增调度计数证据保存。新测试未构建/未执行，不能标记通过；原失败运行没有计数文件，不伪造补齐历史测量。
- 收到billing任务发布协调警告后暂停构建、release替换、云端部署和清理，尚未改TaskManager等生产代码。只读哈希确认release仍为B94C384E53C1742D3C29C7628D2C244CD925F2AF4C8477CCCF3C3C17CCD45C4D。本任务163249发布目的为读取格式码修复，但共享工作树构建不能视作只含该单项变更；需用户确认统一发布窗口后才能继续交付。
- 解冻后顺序：先运行新增回归确认红灯；最小修复任务返回结果到实体的映射，保持源文件/句柄隔离、格式及失败状态；通过完整门禁正常发布；新证据目录重跑已安装APP实际任务→CAD→读取→记录恢复。真实生产账号往返、执行中断、复杂实体/严格原框、磁盘满、支付邮件、公开发行和物理交互等原目标仍未完成。

## A01 生产入口复核与前述严重性纠正（2026-09-16）

- 继续只读检查正式APP装配：ApiClientFactory.Create强制worker，App.xaml.cs的worker分支向TaskManager注入WorkerTranslationService；该适配器Complete明确回填实体译文/状态。因此前述“完成但未输出”只在探针使用的旧本地构造路径复现，不能认定为正式APP默认队列的P0故障。撤回此前过宽严重性描述；旧分支接口问题仍存在，未修复。
- 验收缺口本身需要纠正：CadRuntimeProbe新增worker-pipeline模式，使用安装包WorkerApiClient/WorkerTranslationService及格式恢复、实际TaskManager/读取器/CAD调度/持久化；HTTP仅由内存handler返回固定结果，检查方法/地址/令牌/幂等头/语言/6个条目ID，不能访问生产或扣费。旧pipeline保留便于区分两条路径。
- 发布冻结仍有效；本轮只改测试/说明，没有构建运行新增模式、没有生产源码修改、没有release替换/部署/清理。新模式不算验收通过。解冻后应优先运行正确的worker-pipeline，取得正式路径实际结果，再判断需要哪些最小产品修复，而不是先按旧分支失败修改核心。

## 发布门禁失败归属确认（2026-09-16）

- 协调方已实际运行全Core回归：288通过、1失败、1跳过（290总数）；证据artifacts/installer-safety-20260916-resumed/update-review/core-final.log及core-final.trx。
- 唯一失败Pipeline_RealTranslationService_MapsReturnedPairsBeforeWriteback由本任务新增，归本任务处理。该测试使用真实旧本地TranslationService、TaskManager注入构造、假读取器/假写入器及Offline模式；第299行OutputPath非空断言失败。先前“未执行”状态由此次真实红灯证据更新，不再描述为待确认能否复现。
- 正式APP工厂强制worker，此失败不是正式Worker路径已发生同类故障的证据。测试的来源与覆盖范围明确，但它仍是全量发布门禁的真实阻断，不能删/跳过/弱化或以其它测试通过替代。
- 已向协调任务确认归属。用户未明确授权相关生产实现变更，故本次只读复核并记录，不改任务结果映射/回写/排版代码、不运行构建或发布。推荐待授权后仅修结果衔接，再回归两条路径与输出保护；CAD回写算法保持不动。图标资源已另行修改并本地视觉验证，但安装包/线上网站仍未更新。
- release只读哈希仍B94C384E53C1742D3C29C7628D2C244CD925F2AF4C8477CCCF3C3C17CCD45C4D。

## 任务结果衔接授权修复及回归（2026-09-16）

- 用户明确“授权”后，仅修改TaskManager.cs在翻译返回后调用ApplyTranslationResults。按规范化源文件路径、句柄、原文核对结果，整组核对通过后仅回填TranslatedText/Status/GlossaryHit；错误来源、句柄、原文和重复结果拒绝写回。不修改翻译服务、CAD回写器、排版算法、几何参数或计费。
- 原失败测试保持：本任务修复前重新运行仍失败（artifacts/task-result-handoff-20260916/before.log）。FakeWriter捕获改为调用边界快照，防止同一实体在写回完成后被改为WritebackSuccess而污染传入参数观察；原断言仍要求回写收到Translated状态及正确译文，不删除/跳过/弱化。
- 新增6例：错源文件/错句柄/错原文/重复结果均拒绝且不写输出、原文件不变；已恢复的译文格式直接保留、GlossaryHit保留、失败/跳过状态不混入可写回集合。原真实本地TranslationService用例及既有Worker成功/401/部分重试用例均通过。
- 实际验证：pipeline.log为23通过/0失败；core-full.log为314通过/1既有符号链接权限跳过/0失败（315总数，当前共享工作区包含其他任务新增测试，不将总数增长全部归因于本修复）。git diff --check通过。
- 已向协调任务交接修复范围与日志。没有APP构建、release替换、网站部署或产物清理；release仍163249/B94C384E53C1742D3C29C7628D2C244CD925F2AF4C8477CCCF3C3C17CCD45C4D。图标资源已准备，需协调方统一发布完整门禁后才在实际安装包生效。未把源码回归通过当成已安装APP/CAD全链路完成；正确worker-pipeline的安装版运行及此前全部剩余验收依旧待完成。
