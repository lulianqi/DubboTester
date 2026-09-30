window.dubboTester = {
    load: key => window.localStorage.getItem(key),
    save: (key, value) => window.localStorage.setItem(key, value),
    copy: value => navigator.clipboard.writeText(value ?? ""),
    download: (fileName, content, contentType = "application/json;charset=utf-8") => {
        const blob = new Blob([content ?? ""], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.click();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
