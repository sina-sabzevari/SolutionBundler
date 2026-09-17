# Solution Bundler — WPF

نسخه‌ی مدرن WPF برای انتخاب بخشی از یک Solution و تولید خروجی متنی.

## امکانات

- درخت قابل جست‌وجوی پوشه‌ها و پروژه‌ها با انتخاب چندگانه
- Favorites با امکان افزودن و حذف
- اسکن و انتخاب پسوندها و تخمین اندازه‌ی خروجی پیش از تولید
- نگهداری آخرین مسیر انتخاب‌شده
- خواندن Connection String از فایل‌های `appsettings*.json`
- انتخاب جداول SQL Server به‌صورت Schema → Table و محدودیت تعداد ردیف
- افزودن داده‌های Redis با الگوی کلید و محدودیت تعداد کلید/عضو
- تم Fluent هماهنگ با Light/Dark ویندوز

## اجرا

```powershell
dotnet run --project .\SolutionBundler.Wpf.csproj
```

## انتشار

> **پیش‌نیاز Release:** فایل اجرایی منتشرشده به **.NET 10 Desktop Runtime** نیاز دارد.

```powershell
dotnet publish .\SolutionBundler.Wpf.csproj -c Release --self-contained false -o .\publish
```

فایل اجرایی نهایی در `publish\SolutionBundler.Modern.exe` قرار می‌گیرد و به .NET 10 Desktop Runtime نیاز دارد.
