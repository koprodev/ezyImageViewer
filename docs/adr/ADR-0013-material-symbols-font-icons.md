# ADR-0013: Material Symbols 폰트 아이콘 전환 (UR-006)

- 상태: 채택 (2026-07-17)

## 배경

사용자는 2026-07-17 UI 아이콘을 구글 아이콘으로 교체하도록 지시하고(UR-006) Material Symbols 이름·코드포인트 26종 매핑표를 제공했다(`PingPong.md` [3차] 보존). 적용 방식 결정(Q4)에서 PEER(Codex)는 기존 vector 구조를 보존하는 SVG path 이식을 권고했으나, 사용자는 "svg를 사용하니 UI가 어색함"을 근거로 **Material Symbols 폰트 동봉(FontIcon)** 방식을 최종 선택했다. CLAUDE.md §4-2에 따라 사용자 결정이 PEER 권고를 우선한다.

ADR-0012 결정 1은 버튼 아이콘에 FontIcon 사용을 금지했다. 이 ADR은 그 항목을 대체한다.

## 결정

1. 아이콘 glyph 소스는 동봉한 Material Symbols Outlined variable font 하나로 통일한다.
   - 자산: `EzyImageViewer.App/Assets/Fonts/MaterialSymbolsOutlined.ttf` (Content·산출물 복사).
   - 출처 고정: `google/material-design-icons` commit `abd7f5c0e179c83f068c770650bd14ebac5d5a09`, 원본 TTF SHA-256 `0A186BE334A516CF80A4287073B788FEEF8F0FC2C633C74F4FF7828530F35293`.
   - 배포 자산은 FontTools 4.63.0으로 기본 인스턴스(FILL 0·wght 400·GRAD 0·opsz 24)와 `Icons.xaml`의 55개 코드포인트만 추출한 10,008-byte subset이다. SHA-256은 `6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F`이며 `packaging/subset-material-symbols-font.ps1`로 재현한다.
   - 렌더 기본 인스턴스: FILL 0 · wght 400 · GRAD 0 · opsz 24 (variable font default). 시각 크기는 FontSize 20으로 기존 20×20 grid 계약을 승계한다.
2. 정적 아이콘은 `Icons.xaml`의 `FontIconSource`(`Icon.FontFamily` StaticResource + `Glyph`)로 정의하고, 소비처는 기존 `IconSourceElement + Icon.*` 키 패턴을 유지한다.
3. 반복·교체되는 동적 아이콘(layer eye/lock, palette check)은 `x:String` glyph 리소스를 `IconSourceFor`가 창별 `FontIconSource` 인스턴스로 변환한다.
   - PathIcon Geometry 공유 `ArgumentException`을 우회하던 `XamlBindingHelper` fresh-Geometry 경로는 폰트 전환으로 제거한다.
   - dictionary는 ADR-0012와 동일하게 각 `ViewerWindow.Root.Resources`에서 merge해 다중 창 lifetime 안전성을 유지한다.
4. 매핑 manifest는 `IconSystemContractTests.MaterialSymbolsMapping_MatchesTheApprovedManifest`로 고정한다.
   - 현재 정적 51종 + 동적 6종. 초기 사용자 제공 26종 매핑을 포함한 전체 manifest를 공식 `MaterialSymbolsOutlined.codepoints` 파일로 실측 검증했다(사용자 표 26종 코드포인트 전부 일치).
   - 사용자 표의 조건부 비고를 반영해 `Icon.Image.CropRatio`는 `aspect_ratio`(U+E85B), `Icon.View.Fit`은 `fit_screen`(U+EA10)을 사용한다.
5. Material 등가 glyph가 없는 항목은 다음과 같이 처리한다(사용자 표 비고 준수).
   - 상하 반전: `flip`(U+E3E8) glyph를 사용처에서 90° `RotateTransform`으로 회전.
   - 둥근 사각형·실제 크기 1:1: 기존 20×20 커스텀 `PathIconSource` 2종을 의도적으로 유지.
6. 라이선스: Apache 2.0 전문을 `Assets/Fonts/LICENSE-MaterialSymbols.txt`로 동봉·배포한다. M9-B의 `THIRD-PARTY-NOTICES.md`·SBOM에 통합한다.
7. ADR-0012 결정 1의 vector `PathIconSource` SSOT·FontIcon 금지 조항은 이 ADR로 대체한다. 결정 2~10(36×36 hit target, separator, 두 줄 tooltip, palette/layer 상태, resize render loop, overflow hint, 고정 방향 전환 버튼)과 Bold/Italic 문자 `B`/`I` 유지는 그대로 승계한다.

## 검증 계약

- contract test: 매핑 manifest(키→코드포인트 정확 일치), FontFamily 단일 소스·FontSize 20, 커스텀 vector 2종 한정, FlipVertical 90° 회전 배선, 폰트·라이선스 자산의 SHA-256 exact 일치와 csproj Content, 동적 glyph 문자열, code-behind의 `XamlBindingHelper` 동적 Geometry 변환 경로 제거(커스텀 vector 2종은 `Icons.xaml`에 유지).
- Release build 경고 0·오류 0, 전체 tests 328/328, smoke(`Ready`·dock·layer·fullscreen) exit 0.
- 실제 UI: 가로·세로 rail 캡처에서 전 glyph 렌더(결손 tofu 0), 반전 회전, 커스텀 2종, 하단 chevron/zoom/fullscreen, palette, 비활성 상태, blue overflow hint 확인. 검증용 layout 설정은 바이트·mtime까지 원복했다.
- 잔여 게이트: 사용자 직진 지시로 이번 cycle에서 고대비·DPI·배율 matrix는 미실행. context bar 6종·layer 아이콘의 개별 실캡처, screen reader 발화, 아이콘 80% 사람 식별 시험은 후속 검수로 유지한다.

## 결과와 트레이드오프

- 전체 variable font 대신 기본 인스턴스 55-glyph subset을 배포해 폰트 자산은 10,643,392 bytes에서 10,008 bytes로 줄었다. 새 아이콘을 추가하면 subset과 SHA-256 계약을 함께 갱신해야 한다.
- 커스텀 vector 2종과 폰트 glyph가 혼재하지만 매핑 manifest가 예외를 명시적으로 고정한다.
- FontIcon 계열은 `IsTextScaleFactorEnabled` 기본값 true로 시스템 **텍스트 크기**(display DPI와 별개 축)에 따라 자동 확대될 수 있다. Windows 텍스트 크기 100/150/225% 실측에서 20×20 rail·14×14 palette check·회전 glyph의 clipping/정렬을 확인한 뒤 true 유지 또는 항목별 false를 결정하고 contract로 고정한다. 실측 전 일괄 false는 적용하지 않는다.
- glyph 시각은 Google 공식 디자인을 그대로 사용해 사용자 시각 선호를 반영하며, 식별성은 아이콘 80% 사람 식별 시험으로 판정한다. 신규 아이콘(M5~M7)은 이름·코드포인트 추가만으로 확장된다.
