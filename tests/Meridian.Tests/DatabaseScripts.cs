namespace Meridian.Tests;

/// <summary>Realistic multi-schema DDL scripts per dialect, shared by the database modelling tests.</summary>
internal static class DatabaseScripts
{
    public const string Postgres = """
        -- Shop schema (pg_dump style)
        SET statement_timeout = 0;
        CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
        CREATE SCHEMA sales;
        CREATE SCHEMA IF NOT EXISTS inventory;

        CREATE TYPE sales.order_status AS ENUM ('new', 'paid', 'shipped', 'cancelled');
        CREATE DOMAIN public.email AS varchar(320) CHECK (VALUE ~ '^[^@]+@[^@]+$');
        CREATE SEQUENCE sales.invoice_no START WITH 1000 INCREMENT BY 1;

        CREATE TABLE public.customer (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            email public.email NOT NULL,
            "Display Name" character varying(200),
            credit numeric(10,2) DEFAULT 0 NOT NULL,
            created_at timestamp with time zone DEFAULT now() NOT NULL,
            CONSTRAINT customer_email_uq UNIQUE (email)
        );

        CREATE TABLE inventory.product (
            sku text NOT NULL,
            name text NOT NULL,
            price numeric(12,2) NOT NULL CHECK (price >= 0),
            tags text[],
            CONSTRAINT product_pkey PRIMARY KEY (sku)
        );

        CREATE TABLE sales."order" (
            id serial NOT NULL,
            customer_id bigint NOT NULL,
            status sales.order_status DEFAULT 'new'::sales.order_status NOT NULL,
            total numeric(12,2) GENERATED ALWAYS AS (0) STORED,
            note text COLLATE "C",
            CONSTRAINT order_pkey PRIMARY KEY (id)
        );

        CREATE TABLE sales.order_line (
            order_id integer NOT NULL REFERENCES sales."order"(id) ON DELETE CASCADE,
            line_no smallint NOT NULL,
            sku text NOT NULL,
            quantity integer DEFAULT 1 NOT NULL,
            PRIMARY KEY (order_id, line_no)
        );

        ALTER TABLE ONLY sales."order"
            ADD CONSTRAINT order_customer_fk FOREIGN KEY (customer_id) REFERENCES public.customer(id) ON UPDATE CASCADE ON DELETE RESTRICT;
        ALTER TABLE sales.order_line ADD CONSTRAINT order_line_product_fk FOREIGN KEY (sku) REFERENCES inventory.product (sku);

        CREATE UNIQUE INDEX customer_lower_email ON public.customer USING btree (lower((email)::text));
        CREATE INDEX order_open_idx ON sales."order" USING btree (customer_id) WHERE status <> 'cancelled';
        CREATE INDEX product_tags_gin ON inventory.product USING gin (tags);

        CREATE VIEW sales.open_orders AS
            SELECT o.id, o.customer_id, c.email AS customer_email
            FROM sales."order" o JOIN public.customer c ON c.id = o.customer_id
            WHERE o.status <> 'shipped';
        CREATE MATERIALIZED VIEW sales.daily_totals AS SELECT date_trunc('day', now()) AS day, count(*) AS orders FROM sales."order" WITH NO DATA;

        CREATE FUNCTION sales.touch() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            NEW.created_at := now(); -- semicolons inside the body
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER order_touch BEFORE INSERT OR UPDATE ON sales."order" FOR EACH ROW EXECUTE FUNCTION sales.touch();

        COMMENT ON TABLE public.customer IS 'People who buy things';
        COMMENT ON COLUMN public.customer.credit IS 'Store credit in EUR';
        COMMENT ON SCHEMA sales IS 'Orders and invoicing';
        GRANT SELECT ON ALL TABLES IN SCHEMA sales TO reporting;
        CREATE POLICY p ON sales."order" USING (true);
        """;

    public const string SqlServer = """
        USE [Shop]
        GO
        SET ANSI_NULLS ON
        GO
        CREATE SCHEMA [sales] AUTHORIZATION [dbo]
        GO
        CREATE TABLE [dbo].[Customers](
            [Id] [int] IDENTITY(1,1) NOT NULL,
            [Email] [nvarchar](320) NOT NULL,
            [Name] [nvarchar](max) NULL,
            [IsActive] [bit] NOT NULL CONSTRAINT [DF_Customers_IsActive] DEFAULT ((1)),
            [RowVersion] [timestamp] NOT NULL,
         CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED
        (
            [Id] ASC
        )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF) ON [PRIMARY],
         CONSTRAINT [UQ_Customers_Email] UNIQUE NONCLUSTERED ([Email] ASC)
        ) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
        GO
        CREATE TABLE [sales].[Orders](
            [Id] [int] IDENTITY(1,1) NOT NULL,
            [CustomerId] [int] NOT NULL,
            [PlacedAt] [datetime2](7) NOT NULL,
            [Amount] [decimal](18, 2) NOT NULL,
            [Total] AS ([Amount]*(1.2)) PERSISTED,
         CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id] ASC)
        )
        CREATE TABLE [sales].[OrderNotes](
            [OrderId] [int] NOT NULL,
            [Note] [nvarchar](4000) NULL
        )
        GO
        ALTER TABLE [sales].[Orders] ADD  CONSTRAINT [DF_Orders_PlacedAt]  DEFAULT (sysutcdatetime()) FOR [PlacedAt]
        GO
        ALTER TABLE [sales].[Orders]  WITH CHECK ADD  CONSTRAINT [FK_Orders_Customers] FOREIGN KEY([CustomerId])
        REFERENCES [dbo].[Customers] ([Id])
        ON DELETE CASCADE
        GO
        ALTER TABLE [sales].[Orders] CHECK CONSTRAINT [FK_Orders_Customers]
        GO
        ALTER TABLE [sales].[Orders]  WITH CHECK ADD  CONSTRAINT [CK_Orders_Amount] CHECK  (([Amount]>(0)))
        GO
        ALTER TABLE [sales].[OrderNotes] ADD CONSTRAINT [FK_OrderNotes_Orders] FOREIGN KEY ([OrderId]) REFERENCES [sales].[Orders] ([Id])
        GO
        CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId] ON [sales].[Orders]
        (
            [CustomerId] ASC
        )
        INCLUDE ([PlacedAt]) WHERE ([Amount]>(100))
        GO
        CREATE VIEW [sales].[BigOrders] AS
        SELECT o.Id, o.Amount, c.Email AS CustomerEmail FROM sales.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId WHERE o.Amount > 1000
        GO
        CREATE PROCEDURE [sales].[PlaceOrder] @CustomerId int, @Amount decimal(18,2)
        AS
        BEGIN
            SET NOCOUNT ON;
            INSERT INTO sales.Orders (CustomerId, PlacedAt, Amount) VALUES (@CustomerId, sysutcdatetime(), @Amount);
        END
        GO
        CREATE TRIGGER [sales].[trg_Orders_Audit] ON [sales].[Orders] AFTER INSERT, UPDATE AS
        BEGIN
            SET NOCOUNT ON;
        END
        GO
        EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Registered customers' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'Customers'
        GO
        EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Login e-mail' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'Customers', @level2type=N'COLUMN',@level2name=N'Email'
        GO
        """;

    public const string MySql = """
        CREATE DATABASE IF NOT EXISTS `shop`;
        USE `shop`;
        /*!40101 SET NAMES utf8 */;
        DROP TABLE IF EXISTS `customer`;
        CREATE TABLE `customer` (
          `id` int unsigned NOT NULL AUTO_INCREMENT,
          `email` varchar(320) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL COMMENT 'login',
          `status` enum('active','blocked') NOT NULL DEFAULT 'active',
          `created_at` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          PRIMARY KEY (`id`),
          UNIQUE KEY `uq_customer_email` (`email`),
          KEY `ix_customer_created` (`created_at`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='People who buy';
        # a hash comment
        CREATE TABLE `orders` (
          `id` bigint NOT NULL AUTO_INCREMENT,
          `customer_id` int unsigned NOT NULL,
          `total` decimal(10,2) DEFAULT NULL,
          PRIMARY KEY (`id`),
          KEY `fk_orders_customer` (`customer_id`),
          CONSTRAINT `fk_orders_customer` FOREIGN KEY (`customer_id`) REFERENCES `customer` (`id`) ON DELETE CASCADE
        ) ENGINE=InnoDB;
        CREATE TABLE `analytics`.`visits` (
          `id` bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
          `customer_id` int unsigned,
          FULLTEXT KEY `ft_path` (`path`),
          `path` varchar(500)
        );
        ALTER TABLE `analytics`.`visits` ADD CONSTRAINT `fk_visits_customer` FOREIGN KEY (`customer_id`) REFERENCES `shop`.`customer` (`id`);
        CREATE INDEX ix_orders_total ON orders (total);
        DELIMITER $$
        CREATE DEFINER=`root`@`localhost` PROCEDURE `close_order`(IN p_id BIGINT)
        BEGIN
          UPDATE orders SET total = 0 WHERE id = p_id;
          IF p_id > 0 THEN SELECT 1; END IF;
        END$$
        CREATE TRIGGER `orders_bi` BEFORE INSERT ON `orders` FOR EACH ROW
        BEGIN
          SET NEW.total = COALESCE(NEW.total, 0);
        END$$
        DELIMITER ;
        CREATE VIEW `big_orders` AS SELECT `o`.`id` AS `id`, `o`.`total` AS `total` FROM `orders` `o` WHERE `o`.`total` > 100;
        """;

    public const string Sqlite = """
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS author (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            born DATE
        );
        CREATE TABLE book (
            id INTEGER PRIMARY KEY,
            author_id INTEGER NOT NULL REFERENCES author(id) ON DELETE CASCADE,
            title TEXT NOT NULL,
            isbn TEXT,
            price REAL CHECK (price > 0),
            UNIQUE (author_id, title)
        ) WITHOUT ROWID;
        CREATE TABLE tag (name PRIMARY KEY, colour);
        CREATE UNIQUE INDEX book_isbn ON book(isbn) WHERE isbn IS NOT NULL;
        CREATE VIEW cheap_books AS SELECT title, price FROM book WHERE price < 10;
        CREATE TRIGGER book_ai AFTER INSERT ON book BEGIN UPDATE author SET born = born WHERE id = new.author_id; END;
        INSERT INTO tag VALUES ('x', 'red');
        """;

    public const string Oracle = """
        ALTER SESSION SET CURRENT_SCHEMA = HR;
        CREATE TABLE departments (
            department_id NUMBER(4) CONSTRAINT dept_id_pk PRIMARY KEY,
            department_name VARCHAR2(30 BYTE) NOT NULL,
            manager_id NUMBER(6),
            location_id NUMBER(4)
        ) TABLESPACE users;
        CREATE TABLE employees (
            employee_id NUMBER(6) GENERATED BY DEFAULT AS IDENTITY,
            first_name VARCHAR2(20),
            last_name VARCHAR2(25) CONSTRAINT emp_last_name_nn NOT NULL,
            email VARCHAR2(25) NOT NULL,
            hire_date DATE DEFAULT SYSDATE NOT NULL,
            salary NUMBER(8,2),
            department_id NUMBER(4),
            CONSTRAINT emp_emp_id_pk PRIMARY KEY (employee_id),
            CONSTRAINT emp_salary_min CHECK (salary > 0),
            CONSTRAINT emp_email_uk UNIQUE (email)
        );
        ALTER TABLE employees ADD (CONSTRAINT emp_dept_fk FOREIGN KEY (department_id) REFERENCES departments);
        ALTER TABLE departments ADD CONSTRAINT dept_mgr_fk FOREIGN KEY (manager_id) REFERENCES employees (employee_id);
        CREATE SEQUENCE employees_seq START WITH 207 INCREMENT BY 1 NOCACHE NOCYCLE;
        CREATE INDEX emp_name_ix ON employees (last_name, first_name);
        COMMENT ON TABLE employees IS 'Employees table';
        COMMENT ON COLUMN employees.salary IS 'Monthly salary';
        CREATE OR REPLACE PROCEDURE add_job_history (p_emp_id employees.employee_id%type, p_start DATE)
        IS
        BEGIN
          INSERT INTO job_history (employee_id, start_date) VALUES (p_emp_id, p_start);
        END add_job_history;
        /
        CREATE OR REPLACE VIEW emp_details AS SELECT e.employee_id, e.last_name, d.department_name FROM employees e JOIN departments d ON e.department_id = d.department_id;
        """;
}
