# Windows App SDK 1.8 파일 API 마이그레이션 완료

## 개요
이 프로젝트는 Windows App SDK 1.8에서 도입된 새로운 파일 선택기 API로 성공적으로 마이그레이션되었습니다. 구형 WinRT `Windows.Storage.Pickers` API를 최신 `Microsoft.Windows.Storage.Pickers` API로 교체하여 더 간결하고 안정적인 코드를 달성했습니다.

## 주요 변경사항

### 1. FolderPicker 현대화

#### 이전 (구형 WinRT API):
```csharp
var picker = new Windows.Storage.Pickers.FolderPicker();
WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
picker.FileTypeFilter.Add("*");
var folder = await picker.PickSingleFolderAsync();
string path = folder?.Path;
```

#### 이후 (Windows App SDK 1.8+):
```csharp
var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);
var result = await picker.PickSingleFolderAsync();
string path = result?.Path;
```

### 2. 주요 개선 사항

#### ? 코드 간소화
- **COM Interop 제거**: 더 이상 `WinRT.Interop.InitializeWithWindow.Initialize` 호출 불필요
- **WindowId 직접 사용**: 생성자에서 WindowId를 직접 받아 더 깔끔한 API
- **FileTypeFilter 불필요**: 기본적으로 모든 파일 표시 (선택적으로 설정 가능)

#### ? 향상된 신뢰성
- **경로 기반 결과**: `PickFolderResult`로 문자열 경로 반환 (StorageFolder 객체 대신)
- **승격 시나리오 지원**: 관리자 권한으로 실행되는 앱에서 더 나은 작동
- **데스크톱 우선 설계**: WinUI 3 데스크톱 앱에 최적화

#### ? 일관된 API 패턴
- 모든 새 선택기는 동일한 WindowId 패턴 사용
- StorageFile/StorageFolder 대신 경로 문자열로 통일된 작업

## 영향받은 파일

### 코드 파일
1. **MangaViewer\ViewModels\MangaViewModel.cs**
   - `OpenFolderAsync`: FolderPicker WindowId 패턴 사용
   - 주석 추가하여 새 API 사용 명시

2. **MangaViewer\Services\MangaManager.cs**
   - 레거시 StorageFolder 오버로드 유지 (하위 호환성)
   - 주요 구현은 경로 기반 `LoadFolderAsync(string)` 사용

3. **MangaViewer\Pages\SettingsPage.xaml.cs**
   - `AddLibraryFolder_Click`: 라이브러리 폴더 추가 시 WindowId 패턴 사용

4. **MangaViewer\Services\OcrService.cs**
   - 파일 접근에 `FileRandomAccessStream` 사용 (Windows App SDK 1.8+ 호환)
   - 주석 업데이트하여 현대화 명시

5. **MangaViewer\Services\ImageDecoding.cs**
   - `FileRandomAccessStream.OpenAsync` 사용
   - Windows App SDK 1.8+ 호환성 주석 추가

### 문서
6. **README.md**
   - 새로운 "Modernization notes" 섹션 추가
   - Windows App SDK 1.8 마이그레이션 세부사항 문서화
   - 이전/이후 코드 예제 제공
   - 주요 차이점 및 이점 설명

## Windows App SDK 1.8 새 파일 API의 주요 차이점

| 특징 | 구형 WinRT API | 새 Windows App SDK 1.8 API |
|------|----------------|---------------------------|
| 네임스페이스 | `Windows.Storage.Pickers` | `Microsoft.Windows.Storage.Pickers` |
| 초기화 | `InitializeWithWindow.Initialize(picker, hWnd)` | 생성자에 `WindowId` 전달 |
| 반환 타입 | `StorageFile`, `StorageFolder` | `PickFileResult`, `PickFolderResult` (경로 포함) |
| FileTypeFilter | 필수 (예외 발생) | 선택적 (기본값: 모든 파일) |
| 승격 앱 지원 | 제한적 | 개선됨 |
| HomeGroup | 지원 | 제외됨 (Windows 10에서 더 이상 지원 안 함) |

## 테스트 체크리스트

- [x] 폴더 선택기가 정상적으로 열림 (MangaViewModel)
- [x] 선택한 폴더에서 이미지가 올바르게 로드됨
- [x] 라이브러리 폴더 추가가 정상 작동 (SettingsPage)
- [x] 빌드가 오류 없이 성공
- [x] 모든 파일 접근이 경로 기반으로 작동
- [x] OCR 및 이미지 디코딩이 정상 작동

## 하위 호환성

### 유지된 레거시 메서드
일부 내부 API는 하위 호환성을 위해 레거시 `StorageFile`/`StorageFolder` 오버로드를 유지합니다:

```csharp
// MangaManager.cs - 레거시 지원
public async Task LoadFolderAsync(StorageFolder folder)
{
    if (folder == null) { Clear(); return; }
    await LoadFolderAsync(folder.Path); // 내부적으로 경로 기반 메서드 호출
}

// OcrService.cs - 레거시 지원
public async Task<List<OcrResult>> RecognizeAsync(StorageFile imageFile)
{
    // 레거시 StorageFile 기반 OCR (내부 사용 전용)
}
```

## 향후 개선 사항

1. **FileSavePicker 마이그레이션**: 프로젝트에서 현재 사용하지 않지만, 향후 파일 저장 기능 추가 시 새 API 사용
2. **FileOpenPicker 마이그레이션**: 개별 파일 선택 기능 추가 시 `Microsoft.Windows.Storage.Pickers.FileOpenPicker` 사용
3. **전체 경로 기반 전환**: 모든 내부 API에서 StorageFile/StorageFolder 대신 경로 문자열 사용

## 참고 자료

- [Windows App SDK 1.8 파일 및 폴더 관리](https://learn.microsoft.com/en-us/windows/apps/develop/files/)
- [FolderPicker 사용 가이드](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers)
- [FileSavePicker 사용 가이드](https://learn.microsoft.com/en-us/windows/apps/develop/files/pickers-save-file)
- [WinRT와 Windows App SDK 선택기의 주요 차이점](https://learn.microsoft.com/en-us/windows/apps/develop/files/#use-windows-app-sdk-pickers-to-read-and-write-data)

## 결론

이 마이그레이션을 통해 프로젝트는 최신 Windows App SDK 1.8 표준을 따르게 되었으며, 더 간결하고 유지보수하기 쉬운 코드베이스를 갖추게 되었습니다. 새로운 API는 더 나은 승격 시나리오 지원과 데스크톱 앱 환경에 최적화되어 있습니다.
