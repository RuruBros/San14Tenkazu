# 환경·재해 설정

상단 **편집 오른쪽 환경·재해**에서 사계절 연출, 지역별 비·눈 그래픽과 게임 규칙을 선택한다.
비·눈 그래픽, 비·적설 이동 감소, 날씨 사기 감소, 홍수·산불·가뭄·폭설·폭염을 각각 선택한다.
약함·강함 같은 preset 대신 각 항목의 수치를 직접 입력한다.
연결 직후에는 규칙 OFF다. 이전 선택을 자동 적용하지 않으며 `선택한 조합 적용`으로 저장·시작한다.
`모두 해제`는 계절 연출과 환경 규칙을 모두 중단한다. 이미 발생한 자원 손실은 되돌리지 않는다.

계절 전환 자체는 피해 조건이 아니다. 지역·날짜별 deterministic weather와 해당 지형 조건이 필요하다.
기상은 기존 `Seasons/Assets/climate.json`의 월별 기후 자료를 게임 Hex 위치로 보간한 게임용 모델이다.
기존 게임에서 비가 내린다는 관측이나 실제 역사적 날씨를 읽었다는 뜻은 아니다.
눈은 기온 2°C 이하의 3일 block, 비는 나머지 기온의 일별 추첨으로 결정된다.
비·눈 그래픽은 이 실제 Native 기상 정보를 함께 사용한다. 사계절 OFF에서도 독립적으로 동작하며,
맑은 지역에는 그리지 않는다. 보통 비/큰비와 얕은눈/깊은눈에 따라 입자 밀도를 구분한다.
사계절 적설은 계절·기후 기반이며 랜덤 눈의 누적·융해 상태를 추가하지 않는다.
사기와 재해 피해는 날짜가 연속 진행하여 1·11·21일로 넘어갈 때만 검사한다.
로드·날짜 역행·점프, 연결 직후 및 적용 조합 변경은 피해의 새 baseline을 만든다.
같은 region·seed·date에서는 플레이어와 AI 모두 같은 결과를 받는다.

| 입력값 | 기본값 | 입력 범위 |
| --- | --- | --- |
| 비 / 큰비 이동 감소 | 10 / 20% | 각각 0~99% 정수 |
| 얕은눈 / 깊은눈 이동 감소 | 20 / 40% | 각각 0~99% 정수 |
| 비·눈 노출 사기 감소/순 | 2 | 0~130 정수 |
| 홍수 / 산불 / 가뭄 / 폭설 / 폭염 발생 확률/순 | 각각 3% | 각각 0~100%, 0.1% 단위 |
| 홍수 / 산불 부대 병력 감소 | 각각 5% | 각각 0~99% 정수 |
| 홍수 / 산불 부대 추가 사기 감소 | 각각 4 | 각각 0~130 정수 |
| 홍수 / 산불 / 가뭄 도시 병량 감소 | 각각 6% | 각각 0~100% 정수 |
| 폭설 / 폭염 부대 병력 감소 | 각각 3% | 각각 0~99% 정수 |
| 폭설 / 폭염 부대 추가 사기 감소 | 각각 4 | 각각 0~130 정수 |
| 폭설 / 폭염 도시 병량 감소 | 각각 3% | 각각 0~100% 정수 |

총 23개 값을 독립 입력한다. 입력 0은 해당 수치의 영향을 없앤다. 수상 이동에는 날씨 이동 보정을 적용하지 않는다.
정수 이동 속도는 최소 1이다. 원본 Native 사기 개성 보정과 사기 0의 부대 소멸 처리를 유지한다.
재해 추첨은 조건을 충족한 도시별·순별 입력 확률을 사용한다. 홍수는 큰비·하천·월 강수 80mm 이상,
산불은 맑음·산림·18°C 이상·월 강수 60mm 미만, 가뭄은 맑음·25°C 이상·월 강수 40mm 미만이다.
폭설은 강한 눈(weather code 8) 상태, 폭염은 비·눈 없이 30°C 이상일 때 추첨한다.
월별 기후 값은 당월부터 다음 월까지 날짜에 따라 보간한다. 재해 종류에 월·계절 고정 제한이나 가중치를 추가하지 않았다.
부대 홍수는 하천 인접 칸, 산불은 산림 칸에 적용한다. 가뭄은 도시 병량에만 영향을 준다.
같은 도시에서 여러 재해가 겹치면 가장 큰 병량 피해율을 한 번 적용한다.
육상 부대의 재해 피해는 홍수·산불·폭설·폭염 순서에서 해당하는 하나만 적용하며 날씨 사기 감소는 별도로 더한다.
현재 산불은 이 조건의 즉시 피해 규칙이며, 게임의 불 생성·번짐이나 재해 3D 연출을 추가하지 않는다.
홍수 지형 침수, 화계 확률 및 얼음 도하도 이번 구현의 항목이 아니다.

게임 thread의 원본 이동 함수 `0x269150` 결과를 Native callback에서 보정한다.
진형 수치·이동 진행을 덮어쓰지 않는다. 사기는 원본 `Unit_AddMorale 0x270550`의 deferred 경로를 사용한다.
피해 로그에는 실제 날짜, 도시·칸·부대·대장 ID와 병력·사기·병량의 before/after를 남긴다.
설정은 `seasons/environment/settings.json`, 상세 로그는 `seasons/environment/logs`에 보관한다.
설정 파일은 atomic replace와 이전 hash로 동시 수정 충돌을 검사한다.
이전 version 1·2 설정은 seed·기존 선택·수치를 보존해 읽고 적용할 때 version 3으로 저장한다.
폭설·폭염은 이전 설정에서 자동 선택되지 않는다. 기본 수치는 각자 저장하되 사용자 선택 후에만 적용한다.

렌더링용 `SeasonBridge.dll`과 별도로 `EnvironmentBridge.dll`을 사용한다.
환경 protocol 5는 실제 입력값 전부와 5종 발생 날짜·개발자 테스트 상태를 receipt로 확인한다. 렌더링 protocol 6은 환경 bridge의 weather snapshot 3을 읽으며
날씨 전용 mode에서 원본 지형·수목을 변경하지 않는다.
원본 전체 함수 byte signature, Native bundle와 source snapshot hash, controller identity와 lease를 검사한다.
OFF/연결 해제/종료는 Detours, shared Update slot, memory protection, private allocation 복구를 확인한다.
복구 실패 때 controller를 유지하여 같은 session에서 재시도한다. 다른 DLL basename이나 build로 우회하지 않는다.
같은 editor의 기존 one-shot game-frame 명령과 Native 시작·종료는 공통 dispatch gate로 직렬화한다.
인증된 AppUpdate가 게임 worker를 바꿀 수 있으므로 전체 환경 frame/baseline/알림 처리는 stateLock으로 직렬화한다.
고정 최초 thread ID로 연결을 종료하지 않으며 실제 callback thread와 runtime error는 receipt·복구 로그에 기록한다.

현재 5종 재해·지도 표시·보고 그림 검증과 pins는 [ADR 0017](../../docs/adr/0017-environment-map-indicators.md),
이전 실게임 이동/OFF 증거는 [ADR 0015](../../docs/adr/0015-environment-gameplay-and-settings.md)에 있다.
새 지역별 비·눈의 실게임 화면과 실제 재해 순 진행·로드는 아직 검증하지 않았다.
재해 보고는 모든 세력의 실제 피해를 도시·날짜·종류별로 모아 원본 재난 보고창에 넣는다.
원본 still layout의 그림 17을 해당 보고창 활성 시 한 번 호출한다.
Environment build 18은 원본 음향 API로 재난별 사운드를 한 번 재생한다: 홍수 0x65(FLOOD_WATER), 산불 0x6d(FIRE),
가뭄 0x5f(WIND_STRONG), 폭설 0x60(WIND_COLD), 폭염 0x77(INSECT). 가뭄·폭염은 사용자가 승인한 분위기용 배치다.
원본 allocator/stop 함수의 전체 코드와 다섯 table resource를 인증한다. 원본 음량·비활성 창 음소거는 유지한다.
반복 음원은 최대 6초로 제한하며 알림 닫기·취소·연결 해제 때 반환 channel, sound ID, engine handle의 소유를 대조한 뒤
원본 채널 중단 API를 호출한다. 다른 재생으로 소유가 바뀌면 그 소리를 중단하지 않는다.
중단 확인 실패 시 소유와 복구 대기를 유지한다. 기존 개발자 표시 전용은 선택 음원 ID·channel·요청/중단 상태를 기록한다.
홍수는 원본 그림을 사용하며 산불·가뭄·폭설·폭염은 생성한 전용 그림 4종을 사용한다.
원본 sprite의 BC3 level-0 SHA256을 대조한 뒤 해당 Draw의 texture binding만 교체하고 원래 binding을 복구한다.
인증된 원본 BC3 alpha를 생성 그림 alpha와 곱하여 원래의 불규칙하고 옅은 테두리를 보존한다.
실게임에서 확인된 원본은 mip 1이며, 1~11 mip을 지원하고 생성 그림도 원래 SRV mip 범위를 유지한다.
Batch·Direct Draw·Indexed 및 중첩 GUI 경로를 처리하며 원본 texture identity로 대조한다.
재난 사운드 호출은 원본 Windows x64 ABI대로 두 float 인수를 XMM2/XMM3에 전달한다.
피해 없는 알림 진단은 원본 사운드 호출 여부·audio singleton·반환 channel을 기록한다. 이 기록과 실제 청취는 별도로 확인한다.
원본 asset 파일을 수정하지 않는다. Season build 55의 산불 그림은 실제 원본 hash·Draw/복구·사용자 관찰로 확인했다.
Season build 56의 원본 테두리는 전체 GPU alpha·sRGB variant·lower mip·binding 복구를 독립 검사했다. 새 테두리의 실게임 화면은 아직 확인하지 않았다.
Environment build 17의 보고 문구는 원본 메시지 목록으로 페이지를 나눈다. 표시 전용 4페이지, 실제 피해 3페이지이며
각 페이지에는 직접 LF를 넣지 않고 원본 자동 줄바꿈을 사용한다. 피해 숫자와 테스트 구분·무피해·종료 안내를 모두 유지한다.
원본 다음 메시지 입력으로 페이지를 넘기며 같은 알림이 유지되는 동안 그림·효과음은 한 번만 시작한다.
글자 위젯 크기나 글꼴 배율을 덮어쓰지 않는다. 원본의 긴 문장 가로 맞춤 경로와 한국어 원본 메시지를 대조했으며,
사용자가 build 17/56의 실게임에서 ‘글자 잘 나와’라고 확인했다. 실제 X/Y 배율 숫자는 아직 읽지 못했다.
이전 효과음 channel·원본 음원·전경 gate를 확인했으나 청취는 미확인이었다. 새 build 18의 재난별 효과음 청취도 아직 확인하지 않았다.
로컬 루트 실행 파일의 개발자 메뉴와 공개 빌드 제외는 [ADR 0018](../../docs/adr/0018-local-developer-disaster-tests.md)을 따른다.
하단 기록 헤더 오른쪽 [개발자]를 누르면 도구 창이 바로 열린다. 현재 [재난 테스트] 탭 하나에 기존 액션을 모았으며,
창을 다시 누르면 기존 창을 활성화한다. 테스트 창 닫기는 기존 취소·복구 확인 후 WPF Closing 이벤트가 끝난 뒤 처리한다.
브라우저 검사는 실행하지 않는다. Native build/check는 다음 명령으로 수행한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File source/Environment/Native/BuildEnvironmentBridge.ps1 -OutputDirectory .cache/environment/new-build
dotnet source/RenderChecks/bin/Release/net8.0-windows/San14Tenkazu.Tests.dll --environment-vectors .cache/environment/new-build/vectors.json
dotnet source/RenderChecks/bin/Release/net8.0-windows/San14Tenkazu.Tests.dll --environment-bundle-check source/Environment
```
