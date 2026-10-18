# 反射/序列化点为自有实现，保留数据类与枚举名以便线协议按名称解析。
-keepclassmembers class com.fufucode.mobile.data.** { *; }
-keepattributes Signature, *Annotation*, InnerClasses, EnclosingMethod
-dontwarn org.jetbrains.annotations.**
