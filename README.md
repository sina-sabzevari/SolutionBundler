# Solution Bundler v10

برنامه‌ی Windows برای انتخاب پروژه‌ها، پوشه‌ها و پسوندهای موردنیاز از یک Solution و تجمیع محتوای آن‌ها در یک فایل متنی. این نسخه از SQL Server و Redis نیز پشتیبانی می‌کند.

## دانلود و اجرا

[دانلود فایل اجرایی SolutionBundler.Modern v10](releases/SolutionBundler.Modern-v10-win-x64.exe)

> **پیش‌نیاز اجرا:** این نسخه به **.NET 10 Desktop Runtime** روی Windows x64 نیاز دارد.

پس از دانلود، فایل `SolutionBundler.Modern-v10-win-x64.exe` را اجرا کنید. اگر .NET 10 Desktop Runtime روی سیستم نصب نباشد، Windows هنگام اجرا پیام نصب Runtime را نمایش می‌دهد.

## امکانات نسخه ۱۰

- تولید مستقل خروجی TXT و ZIP
- نگهداری ساختار پوشه‌ها و فایل‌های انتخابی در ZIP
- خروجی SQL Server با پسوند `.sql`
- خروجی Redis با پسوند `.redis`
- پنهان‌کردن هم‌زمان چند پوشه یا پروژه
- نمایش سلسله‌مراتبی و بازیابی پوشه‌های پنهان
- ذخیره تنظیمات Hidden و Favorites

## سورس برنامه

سورس نسخه‌ی مدرن WPF در پوشه‌ی [`SolutionBundler.Wpf`](SolutionBundler.Wpf) قرار دارد.

## حریم خصوصی و امضای کد

- [Privacy Policy](PRIVACY.md)
- [Code signing policy](CODE_SIGNING_POLICY.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

این پروژه تحت [مجوز MIT](LICENSE) منتشر می‌شود. Build رسمی فایل اجرایی توسط Workflow موجود در `.github/workflows/release-build.yml` تولید می‌شود. پروژه برای دریافت امضای رایگان از SignPath Foundation آماده شده است؛ تا زمان پذیرش و فعال‌شدن امضا، فایل‌های Release با عنوان unsigned منتشر می‌شوند.
