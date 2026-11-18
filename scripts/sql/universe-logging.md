# Statement capture for Rocket UniVerse / UniData (U2)

Many ERP packages (DDI inForm among them) keep their data on a UniVerse server. The client PC talks to it
over the UniRPC port (31438 by default), which App Watcher's discovery reports as `universe`. The data is not
a file the watcher can read, so there are three ways to get tables and rules:

## 1. A read-only ODBC login (best)

The vendor's install usually includes the **UniVerse ODBC driver** (UVODBC). Ask the vendor or the server
administrator for a read-only account, then in App Watcher: Settings > Data > **Add ODBC connection**,
pick the DSN (or type `DSN=<name>;UID=<user>;PWD=<password>`), and list the files you care about
(`CUSTOMER,PRODUCT,INVOICE,...`); a U2 account has thousands of files, so name the ones the screens use.
App Watcher then takes a read-only before/after copy around each save, like any other database.

Multivalued fields come through the ODBC driver as the schema's HS_* mapping defines; if a file has no
schema, the driver cannot see it and the export route below is the fallback.

## 2. A nightly export drop folder (cheapest)

Most sites already have scheduled exports (CSV, or `LIST ... TOXML` / `TO DELIM`) of the key files. Point
Settings > Data > **Add export drop folder** at the folder they land in, one file per table with a header
row. App Watcher snapshots and diffs those files, so it learns which columns change after which screen even
without a login to the server.

## 3. Statement logging on the server (exact)

UniVerse can log UniRPC and SQL activity on the server side:

- **UVSQL logging**: enable SQL statement logging in the `uvconfig` / `UV.ACCOUNT` logging options the vendor
  documents for your release (the parameter names differ by version; `LOGBLSZ`/`LOGBLNUM` control the log
  files). The log is a text file; copy it to a folder on the watched PC and point Settings > Data >
  **SQL statement log** at it. App Watcher reads lines that start with a timestamp and contain
  INSERT / UPDATE / DELETE.
- **Application-level logging**: ask the vendor whether the client can log the records it writes
  (many U2 applications have a transaction log or audit file). Export it nightly into the drop folder.

Nothing here reads or changes business data; all three routes are read-only towards the server.
